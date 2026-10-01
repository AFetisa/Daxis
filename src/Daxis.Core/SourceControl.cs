using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Daxis.Core;

public enum GitState { NotConnected, Connected, Initialized }

/// <summary>Where a workspace points: provider, repo, folder and branch.</summary>
public sealed record GitProvider(string Type, string? Organization, string? Project, string? Owner, string Repository,
    string Branch, string Directory, string? CustomDomain = null)
{
    public bool IsGitHub => Type == "GitHub";
    public string Account => IsGitHub ? Owner ?? "" : $"{Organization}/{Project}";
    public string Repo => $"{Account}/{Repository}";
    public string Folder => "/" + Directory.Trim('/');
    /// <summary>Same repo + folder: workspaces in one family differ only by branch.</summary>
    public string FamilyKey => $"{Type}|{Account}|{Repository}|{Folder}".ToLowerInvariant();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["gitProviderType"] = Type, ["repositoryName"] = Repository, ["branchName"] = Branch, ["directoryName"] = Directory };
        if (IsGitHub)
        {
            o["ownerName"] = Owner;
            if (CustomDomain is not null) o["customDomainName"] = CustomDomain;
        }
        else
        {
            o["organizationName"] = Organization;
            o["projectName"] = Project;
        }
        return o;
    }
}

public sealed record GitConnection(GitState State, GitProvider? Provider, string? Head, DateTimeOffset? LastSync, bool Selective = false);

/// <summary>One item's difference between workspace and branch. Change values: Added, Modified, Deleted (or null).</summary>
public sealed record GitChange(string? ObjectId, string? LogicalId, string Type, string Name, string? WorkspaceChange, string? RemoteChange, string Conflict)
{
    public bool IsConflict => Conflict == "Conflict";
}

public sealed record GitStatus(string? WorkspaceHead, string? RemoteCommitHash, IReadOnlyList<GitChange> Changes)
{
    public int Uncommitted => Changes.Count(c => c.WorkspaceChange is not null && !c.IsConflict);
    public int Incoming => Changes.Count(c => c.RemoteChange is not null && !c.IsConflict);
    public int Conflicts => Changes.Count(c => c.IsConflict);
    public bool Clean => Changes.Count == 0;
}

/// <summary>Source: Automatic (Azure DevOps SSO), ConfiguredConnection (a stored connection) or None.</summary>
public sealed record GitCredentials(string Source, string? ConnectionId = null)
{
    public bool IsConfigured => Source is "Automatic" or "ConfiguredConnection";
    public JsonObject ToJson() => ConnectionId is null
        ? new JsonObject { ["source"] = Source }
        : new JsonObject { ["source"] = Source, ["connectionId"] = ConnectionId };
}

public sealed record InitResult(string RequiredAction, string? WorkspaceHead, string? RemoteCommitHash);

public sealed record WorkspaceRelation(string Id, string WorkspaceId, string RelatedWorkspaceId, string Type);

public enum Freshness { Unknown, Fresh, Stale, Critical }

/// <summary>A repo + folder and the workspaces connected to it, one per branch (ideally).</summary>
public sealed record GitFamily(string Key, string Label, IReadOnlyList<FamilyMember> Members);
public sealed record FamilyMember(string WorkspaceId, string Branch, bool SharesBranch);

public enum Impact { Create, Overwrite, Delete }

/// <summary>What a branch switch would do to one item, and whether that is allowed.</summary>
public sealed record PlannedChange(string Name, string Type, Impact Impact, bool DataBearing, string? Block, string? Warning);

public sealed record Check(string Title, bool Ok, string Detail);

public static class Git
{
    // ── Parsing ──────────────────────────────────────────────────────────────

    public static GitConnection ParseConnection(JsonNode n)
    {
        var state = S(n, "gitConnectionState") switch
        {
            "ConnectedAndInitialized" => GitState.Initialized,
            "Connected" => GitState.Connected,
            _ => GitState.NotConnected,
        };
        var p = n["gitProviderDetails"];
        var provider = p is null ? null : new GitProvider(S(p, "gitProviderType") ?? "", S(p, "organizationName"), S(p, "projectName"),
            S(p, "ownerName"), S(p, "repositoryName") ?? "", S(p, "branchName") ?? "", S(p, "directoryName") ?? "/", S(p, "customDomainName"));
        var sync = n["gitSyncDetails"];
        DateTimeOffset? last = DateTimeOffset.TryParse(sync is null ? null : S(sync, "lastSyncTime"), out var t) ? t : null;
        return new GitConnection(state, provider, sync is null ? null : S(sync, "head"), last, S(n, "gitConnectionType") == "Selective");
    }

    public static GitStatus ParseStatus(JsonNode n) => new(S(n, "workspaceHead"), S(n, "remoteCommitHash"),
        (n["changes"]?.AsArray() ?? []).Select(c =>
        {
            var m = c!["itemMetadata"];
            var id = m?["itemIdentifier"];
            return new GitChange(id is null ? null : S(id, "objectId"), id is null ? null : S(id, "logicalId"),
                m is null ? "" : S(m, "itemType") ?? "", m is null ? "" : S(m, "displayName") ?? "",
                S(c, "workspaceChange"), S(c, "remoteChange"), S(c, "conflictType") ?? "None");
        }).ToList());

    public static GitCredentials ParseCredentials(JsonNode n) => new(S(n, "source") ?? "None", S(n, "connectionId"));

    public static InitResult ParseInit(JsonNode n) => new(S(n, "requiredAction") ?? "None", S(n, "workspaceHead"), S(n, "remoteCommitHash"));

    public static WorkspaceRelation ParseRelation(JsonNode n) =>
        new(S(n, "id") ?? "", S(n, "workspaceId") ?? "", S(n, "relatedWorkspaceId") ?? "", S(n, "relationType") ?? "");

    internal static JsonObject Identifier(GitChange c)
    {
        var o = new JsonObject();
        if (c.ObjectId is not null) o["objectId"] = c.ObjectId;
        if (c.LogicalId is not null) o["logicalId"] = c.LogicalId;
        return o;
    }

    static string? S(JsonNode n, string prop) => n[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ── Scan ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Conflicts, a connection that was never initialised, or no sync within <paramref name="criticalDays"/> → Critical;
    /// no sync within <paramref name="staleDays"/> → Stale.
    /// </summary>
    public static Freshness FreshnessOf(GitConnection? c, GitStatus? s, DateTimeOffset now, int staleDays, int criticalDays)
    {
        if (c is null || c.State == GitState.NotConnected) return Freshness.Unknown;
        if (c.State == GitState.Connected || c.LastSync is null || s?.Conflicts > 0) return Freshness.Critical;
        var age = now - c.LastSync.Value;
        return age.TotalDays > criticalDays ? Freshness.Critical : age.TotalDays > staleDays ? Freshness.Stale : Freshness.Fresh;
    }

    public static string Ago(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is null) return "Never";
        var d = now - t.Value;
        return d.TotalMinutes < 1 ? "Just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 60 ? $"{(int)d.TotalDays} d ago"
            : $"{(int)(d.TotalDays / 30)} months ago";
    }

    /// <summary>Groups connected workspaces by repo + folder. Two workspaces on the same branch there overwrite each other's commits.</summary>
    public static List<GitFamily> Families(IEnumerable<(string WorkspaceId, GitProvider Provider)> connected) =>
        connected.GroupBy(c => c.Provider.FamilyKey)
            .Select(g =>
            {
                var branches = g.GroupBy(m => m.Provider.Branch, StringComparer.OrdinalIgnoreCase).ToDictionary(b => b.Key, b => b.Count(), StringComparer.OrdinalIgnoreCase);
                var first = g.First().Provider;
                return new GitFamily(g.Key, $"{first.Repo}  {first.Folder}",
                    g.OrderBy(m => m.Provider.Branch, StringComparer.OrdinalIgnoreCase)
                        .Select(m => new FamilyMember(m.WorkspaceId, m.Provider.Branch, branches[m.Provider.Branch] > 1)).ToList());
            })
            .OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToList();

    // ── Item types ───────────────────────────────────────────────────────────

    /// <summary>
    /// Item types Fabric Git integration tracks (learn.microsoft.com, Sept 2026). Anything else is ignored by Git and is
    /// a candidate for offload. ponytail: static list; Microsoft adds types every few months — update it then.
    /// </summary>
    public static readonly HashSet<string> Tracked = new(StringComparer.OrdinalIgnoreCase)
    {
        "Environment", "GraphQLApi", "Lakehouse", "Notebook", "SparkJobDefinition", "UserDataFunction",
        "MLExperiment", "MLModel", "DataAgent", "CopyJob", "Dataflow", "DataPipeline", "MirroredDatabase",
        "MountedDataFactory", "SnowflakeDatabase", "ApacheAirflowJob", "DbtJob", "OperationsAgent", "Reflex",
        "Eventhouse", "Eventstream", "KQLDatabase", "KQLQueryset", "KQLDashboard", "Map", "EventSchemaSet",
        "DigitalTwinBuilder", "AnomalyDetector", "Warehouse", "MirroredAzureDatabricksCatalog", "MetricSet",
        "OrgApp", "PaginatedReport", "Report", "SemanticModel", "SQLDatabase", "CosmosDBDatabase", "GraphModel",
        "GraphQuerySet", "DeploymentPlan", "VariableLibrary", "Ontology", "Plan",
    };

    /// <summary>Created and owned by another item (a lakehouse's SQL endpoint); never offloaded on their own.</summary>
    public static readonly HashSet<string> SystemOwned = new(StringComparer.OrdinalIgnoreCase) { "SQLEndpoint", "MirroredWarehouse" };

    /// <summary>
    /// Items whose data lives in the item. Git only carries their definition, so deleting one deletes the data and a later
    /// sync brings back an empty shell.
    /// </summary>
    public static readonly HashSet<string> DataBearing = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lakehouse", "Warehouse", "SQLDatabase", "Eventhouse", "KQLDatabase", "MirroredDatabase", "CosmosDBDatabase",
        "SnowflakeDatabase", "MirroredAzureDatabricksCatalog", "MLModel", "MLExperiment",
    };

    public static bool IsOffloadCandidate(FabricItem i) => !Tracked.Contains(i.Type) && !SystemOwned.Contains(i.Type);

    // ── Repoint safety ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the status taken right after connecting to the target branch as the effect of the full update that follows.
    /// Items listed only on the workspace side exist nowhere in the target branch, and the first update after connecting
    /// overwrites the whole workspace, so they count as deletions. Items not listed are identical on both sides.
    /// </summary>
    public static List<PlannedChange> ClassifySwitch(GitStatus status) => status.Changes.Select(c =>
    {
        var impact = c.RemoteChange switch
        {
            "Added" => Impact.Create,
            "Deleted" => Impact.Delete,
            "Modified" => Impact.Overwrite,
            _ => c.IsConflict ? Impact.Overwrite
                : c.WorkspaceChange == "Added" ? Impact.Delete
                : c.WorkspaceChange == "Deleted" ? Impact.Create
                : Impact.Overwrite,
        };
        var data = DataBearing.Contains(c.Type);
        string? block = data && impact == Impact.Delete
            ? $"{c.Name} ({c.Type}) is not in the target branch. Switching would delete it and the data stored in it."
            : null;
        // Lakehouse tables and files are never touched by Git; other stores apply schema from Git, which can drop objects.
        string? warn = data && impact == Impact.Overwrite && !c.Type.Equals("Lakehouse", StringComparison.OrdinalIgnoreCase)
            ? $"{c.Name} ({c.Type}) gets the target branch's schema. Objects missing there may be dropped with their data."
            : null;
        return new PlannedChange(c.Name, c.Type, impact, data, block, warn);
    }).OrderBy(p => p.Impact == Impact.Delete ? 0 : p.Impact == Impact.Overwrite ? 1 : 2).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// What a normal Update from Git would do: incoming changes, plus conflicts when the Git version wins. Workspace-only
    /// changes stay as uncommitted. Removing a data-bearing item is blocked here too: do that deliberately in Fabric.
    /// </summary>
    public static List<PlannedChange> ClassifyUpdate(GitStatus status, string? conflictPolicy) => status.Changes
        .Where(c => c.IsConflict ? conflictPolicy == "PreferRemote" : c.RemoteChange is not null)
        .Select(c =>
        {
            var impact = c.RemoteChange switch { "Added" => Impact.Create, "Deleted" => Impact.Delete, _ => Impact.Overwrite };
            var data = DataBearing.Contains(c.Type);
            return new PlannedChange(c.Name, c.Type, impact, data,
                data && impact == Impact.Delete ? $"{c.Name} ({c.Type}) was removed in Git. Updating would delete it and its data; delete it in Fabric yourself if that is intended." : null,
                data && impact == Impact.Overwrite && !c.Type.Equals("Lakehouse", StringComparison.OrdinalIgnoreCase)
                    ? $"{c.Name} ({c.Type}) takes the schema from Git. Objects missing there may be dropped with their data." : null);
        })
        .OrderBy(p => p.Impact == Impact.Delete ? 0 : p.Impact == Impact.Overwrite ? 1 : 2).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // Conservative subset of git-check-ref-format.
    static readonly Regex BranchName = new(@"^(?!/)(?!.*//)(?!.*\.\.)(?!.*@\{)(?!.*\.lock$)(?!.*[/.]$)[^\s~^:?*\[\\\x00-\x1f\x7f]{1,250}$");

    public static bool IsValidBranch(string? name) => name is not null && BranchName.IsMatch(name);

    /// <summary>Everything that must hold before a repoint touches the workspace. Any failure blocks it.</summary>
    public static List<Check> Preflight(GitConnection conn, GitStatus? status, string? statusError, GitCredentials? creds,
        IReadOnlyList<FabricItem> items, string? target, string? backupFolder)
    {
        var dupes = items.Where(i => !SystemOwned.Contains(i.Type))
            .GroupBy(i => (i.Type.ToLowerInvariant(), i.DisplayName.ToLowerInvariant())).Where(g => g.Count() > 1)
            .Select(g => $"{g.First().DisplayName} ({g.First().Type})").ToList();
        return
        [
            new("Workspace is connected and initialised", conn.State == GitState.Initialized && conn.Provider is not null,
                conn.State == GitState.Initialized ? $"{conn.Provider?.Repo} · {conn.Provider?.Branch}" : "Connect and sync it in Fabric first."),
            new("Your Git credentials are set up", creds?.IsConfigured == true && (conn.Provider?.IsGitHub != true || creds.ConnectionId is not null),
                creds?.IsConfigured == true ? creds.Source : "Set up your Git account in the workspace's Source control panel."),
            new("Nothing uncommitted", status is not null && status.Uncommitted == 0 && status.Conflicts == 0,
                status is null ? $"Couldn't read Git status: {statusError}"
                : status.Uncommitted + status.Conflicts == 0 ? "Every item matches a commit, so Git holds a copy of it."
                : $"{status.Uncommitted} uncommitted change(s), {status.Conflicts} conflict(s). Commit them first or they are lost."),
            new("Target branch is valid", IsValidBranch(target) && !string.Equals(target, conn.Provider?.Branch, StringComparison.Ordinal),
                !IsValidBranch(target) ? "Enter an existing branch name." : target == conn.Provider?.Branch ? "That is the current branch." : target!),
            new("No duplicate item names", dupes.Count == 0, dupes.Count == 0 ? "Git needs unique names per type." : string.Join(", ", dupes)),
            new("Backup folder chosen", !string.IsNullOrWhiteSpace(backupFolder),
                string.IsNullOrWhiteSpace(backupFolder) ? "Choose a local folder; every item is backed up there first." : backupFolder!),
        ];
    }
}

// ── Repoint journal ─────────────────────────────────────────────────────────

public enum RepointStep { Started, BackedUp, Disconnected, Connected, Initialized, Previewed, Updating, Done, RollingBack, RolledBack }

/// <summary>
/// Durable record of a repoint in progress. Written before every step that changes the service, so a crash or closed app
/// can always put the workspace back on its original branch.
/// </summary>
public sealed class RepointJournal
{
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceName { get; set; } = "";
    public GitProvider Original { get; set; } = null!;
    public string? OriginalHead { get; set; }
    public GitCredentials Credentials { get; set; } = new("None");
    public string Target { get; set; } = "";
    public string? BackupPath { get; set; }
    public List<WorkspaceRelation> Relations { get; set; } = [];
    public string? RemoteCommitHash { get; set; }
    public string? TargetWorkspaceHead { get; set; }
    public string? RequiredAction { get; set; }
    /// <summary>Set before the update starts: from then on a restore must also pull the original content back.</summary>
    public bool ContentChanged { get; set; }
    public RepointStep Step { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Log { get; set; } = [];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder => Path.Combine(AppPaths.Data, "journal");
    string FilePath(string dir) => Path.Combine(dir, WorkspaceId + ".json");

    /// <summary>The workspace is (or may be) off its original branch: a restore is needed before it can be left alone.</summary>
    [JsonIgnore]
    public bool NeedsRecovery => Step is RepointStep.Disconnected or RepointStep.Connected or RepointStep.Initialized
        or RepointStep.Previewed or RepointStep.Updating or RepointStep.RollingBack;

    [JsonIgnore] public bool Finished => Step is RepointStep.Done or RepointStep.RolledBack;

    public void Advance(RepointStep step, string note, string? dir = null)
    {
        Step = step;
        Log.Add($"{DateTimeOffset.Now:HH:mm:ss} {step}: {note}");
        Save(dir);
    }

    public void Save(string? dir = null)
    {
        dir ??= Folder;
        Directory.CreateDirectory(dir);
        var path = FilePath(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true); // atomic replace: never a half-written journal
    }

    public void Delete(string? dir = null) => File.Delete(FilePath(dir ?? Folder));

    public static RepointJournal? Load(string path)
    {
        try { return JsonSerializer.Deserialize<RepointJournal>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static List<RepointJournal> All(string? dir = null)
    {
        dir ??= Folder;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(Load).OfType<RepointJournal>().ToList()
            : [];
    }
}
