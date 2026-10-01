using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daxis.Core;

/// <summary>One file of an exported item, as raw bytes (images and custom visuals are binary).</summary>
public sealed record ItemFile(string Path, byte[] Content);

/// <summary>What happened to one item in an offload or backup run.</summary>
public sealed record OffloadEntry(string ItemId, string Type, string Name, string Folder, string Method, string? Note = null)
{
    public bool Ok => Method != Offload.Failed;
    /// <summary>The item's content was saved (definition or PBIX), not just a descriptor. Only this counts as a backup.</summary>
    public bool IsFullCopy => Method is Offload.Definition or Offload.Pbix;
}

public sealed record OffloadManifest(DateTimeOffset At, string WorkspaceId, string WorkspaceName, string Kind, List<OffloadEntry> Items)
{
    public int Saved => Items.Count(i => i.Ok);
    public int FailedCount => Items.Count(i => !i.Ok);
}

public sealed record OffloadProgress(int Done, int Total, string Current);

/// <summary>
/// Writes item definitions to a local folder in the Fabric Git layout (<c>&lt;Name&gt;.&lt;Type&gt;/…</c>), so a copy made
/// here can later be committed to a repo or re-imported. Used for items Git can't track (offload) and for the
/// full backup taken before a repoint. Local-only: nothing is written to the service.
/// </summary>
public static class Offload
{
    public const string Definition = "Definition", Pbix = "PBIX", Metadata = "Metadata", Failed = "Failed";
    public const string ManifestFile = "_offload.json";
    const string BackupFolder = "_backups";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Windows is the strictest target: strip its invalid characters and trailing dots/spaces.</summary>
    public static string Sanitise(string name)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));
        var s = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Item id → folder name. Names that collide case-insensitively get a short id suffix so nothing overwrites anything.</summary>
    public static Dictionary<string, string> FolderNames(IEnumerable<FabricItem> items)
    {
        var list = items.ToList();
        var counts = list.GroupBy(i => Base(i), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return list.ToDictionary(i => i.Id, i =>
        {
            var b = Base(i);
            return counts[b] == 1 ? b : $"{Sanitise(i.DisplayName)} ({i.Id[..Math.Min(8, i.Id.Length)]}).{i.Type}";
        });

        static string Base(FabricItem i) => $"{Sanitise(i.DisplayName)}.{i.Type}";
    }

    public static string WorkspaceRoot(string root, Workspace ws) => Path.Combine(root, Sanitise(ws.DisplayName));

    public static string BackupRoot(string root, Workspace ws, string branch, DateTimeOffset at) =>
        Path.Combine(WorkspaceRoot(root, ws), BackupFolder, $"{at.ToLocalTime():yyyyMMdd-HHmmss}-{Sanitise(branch)}");

    /// <summary>
    /// Writes one item's files under <paramref name="itemDir"/>, replacing the previous copy only once the new one is
    /// complete (a failed export never leaves a half-written folder or destroys the last good copy).
    /// </summary>
    public static void WriteItem(string itemDir, IEnumerable<ItemFile> files)
    {
        var full = Path.GetFullPath(itemDir);
        var tmp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in files)
            {
                var target = SafeCombine(tmp, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, f.Content);
            }
            var old = full + ".old-" + Guid.NewGuid().ToString("N")[..8];
            if (Directory.Exists(full)) Directory.Move(full, old);
            Directory.Move(tmp, full);
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
        }
        catch
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            throw;
        }
    }

    /// <summary>Joins a service-supplied relative path onto a folder, refusing anything that would escape it.</summary>
    internal static string SafeCombine(string dir, string relative)
    {
        var segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new InvalidOperationException($"Refusing unsafe path from the service: {relative}");
        var path = Path.GetFullPath(Path.Combine([dir, .. segments.Select(Sanitise)]));
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing unsafe path from the service: {relative}");
        return path;
    }

    public static void WriteManifest(string dir, OffloadManifest m)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(m, Json));
    }

    public static OffloadManifest? ReadManifest(string dir)
    {
        try { return JsonSerializer.Deserialize<OffloadManifest>(File.ReadAllText(Path.Combine(dir, ManifestFile)), Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Exports <paramref name="items"/> into <paramref name="dir"/>: the item definition when the API has one, a PBIX
    /// for reports without one, otherwise a metadata record so the inventory is never silently incomplete.
    /// </summary>
    public static async Task<OffloadManifest> RunAsync(FabricClient fabric, Workspace ws, IReadOnlyList<FabricItem> items, string dir,
        string kind, IProgress<OffloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var folders = FolderNames(items);
        var entries = new OffloadEntry[items.Count];
        var done = 0;
        using var gate = new SemaphoreSlim(4); // ponytail: fixed fan-out; the client already backs off on 429
        await Task.WhenAll(items.Select(async (item, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { entries[i] = await ExportOneAsync(fabric, item, Path.Combine(dir, folders[item.Id]), folders[item.Id], ct).ConfigureAwait(false); }
            finally
            {
                gate.Release();
                progress?.Report(new OffloadProgress(Interlocked.Increment(ref done), items.Count, item.DisplayName));
            }
        })).ConfigureAwait(false);
        var manifest = new OffloadManifest(DateTimeOffset.UtcNow, ws.Id, ws.DisplayName, kind, [.. entries]);
        WriteManifest(dir, manifest);
        return manifest;
    }

    static async Task<OffloadEntry> ExportOneAsync(FabricClient fabric, FabricItem item, string itemDir, string folder, CancellationToken ct)
    {
        string? why = null;
        try
        {
            // Off the UI thread from here: exports write many files.
            var files = await fabric.GetDefinitionFilesAsync(item, item.Type == "SemanticModel" ? "TMDL" : null, ct).ConfigureAwait(false);
            WriteItem(itemDir, files);
            return new(item.Id, item.Type, item.DisplayName, folder, Definition);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { why = e.Message; }

        try
        {
            if (item.Type == "Report")
            {
                var pbix = await fabric.ExportReportAsync(item, ct).ConfigureAwait(false);
                WriteItem(itemDir, [new ItemFile(Sanitise(item.DisplayName) + ".pbix", pbix), Meta(item, why)]);
                return new(item.Id, item.Type, item.DisplayName, folder, Pbix, "No item definition; exported the PBIX instead.");
            }
            var extra = await fabric.ItemMetadataAsync(item, ct).ConfigureAwait(false);
            WriteItem(itemDir, [Meta(item, why, extra)]);
            return new(item.Id, item.Type, item.DisplayName, folder, Metadata, "No exportable definition; saved its metadata only.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // Keep whatever copy an earlier run left: a failure must never destroy the last good export.
            return new(item.Id, item.Type, item.DisplayName, folder, Failed, e.Message);
        }
    }

    static ItemFile Meta(FabricItem item, string? definitionError, JsonElement? extra = null) =>
        new("item.metadata.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = item.Id,
            type = item.Type,
            displayName = item.DisplayName,
            description = item.Description,
            workspaceId = item.WorkspaceId,
            exportedAt = DateTimeOffset.UtcNow,
            definitionError,
            details = extra,
        }, Json));
}
