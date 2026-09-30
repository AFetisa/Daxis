using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Daxis.Core;

public sealed record Workspace(string Id, string DisplayName, string Type, string? CapacityId = null);
public sealed record CapacityInfo(string Id, string Name, string Sku, string Region, string State);
public sealed record FabricItem(string Id, string DisplayName, string Type, string? Description, string WorkspaceId);
public sealed record DefinitionPart(string Path, string Payload, string PayloadType);
public sealed record LakehouseInfo(string? SqlEndpoint, string? TablesPath, string? FilesPath);
public sealed record LakehouseTable(string Name, string Type, string Format, string Location);
public sealed record OneLakeEntry(string Name, string FullPath, bool IsDirectory, long Size, string Modified);
public sealed record JobState(string Status, string? Failure)
{
    public bool IsFinal => Status is "Completed" or "Failed" or "Cancelled" or "Deduped";
}
public sealed record ReportRef(string Id, string Name, string WorkspaceId, string WorkspaceName, string WebUrl);
/// <summary>A report and the semantic model it's bound to (which can live in another workspace).</summary>
public sealed record WorkspaceReport(ReportRef Ref, string ModelId, string ModelWorkspaceId);
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows);

public sealed class FabricException(HttpStatusCode status, string message, string? code = null) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    /// <summary>The service's <c>errorCode</c> (e.g. WorkspaceNotConnectedToGit), when it sent one.</summary>
    public string? Code { get; } = code;
}

/// <summary>Thin client over the Fabric, Power BI and OneLake REST APIs.</summary>
public sealed partial class FabricClient(Auth auth) : IDisposable
{
    const string Fabric = "https://api.fabric.microsoft.com/v1/";
    const string PowerBi = "https://api.powerbi.com/v1.0/myorg/";
    const string OneLake = "https://onelake.dfs.fabric.microsoft.com/";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // ── Workspaces & items ───────────────────────────────────────────────────

    public async Task<List<Workspace>> WorkspacesAsync(CancellationToken ct = default) =>
        (await PagedAsync(Fabric + "workspaces", ct))
            .Select(n => new Workspace(Str(n, "id"), Str(n, "displayName"), Str(n, "type"), n["capacityId"]?.GetValue<string>()))
            .OrderBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<List<FabricItem>> ItemsAsync(string workspaceId, CancellationToken ct = default) =>
        (await PagedAsync($"{Fabric}workspaces/{E(workspaceId)}/items", ct))
            .Select(n => new FabricItem(Str(n, "id"), Str(n, "displayName"), Str(n, "type"),
                n["description"]?.GetValue<string>(), workspaceId))
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Capacities the signed-in user can see (admins and contributors of the capacity).</summary>
    public async Task<List<CapacityInfo>> CapacitiesAsync(CancellationToken ct = default) =>
        (await PagedAsync(Fabric + "capacities", ct))
            .Select(n => new CapacityInfo(Str(n, "id"), Str(n, "displayName"), Str(n, "sku"), Str(n, "region"), Str(n, "state")))
            .ToList();

    // ── Item definitions (notebooks, any item) ───────────────────────────────

    public async Task<List<DefinitionPart>> GetDefinitionAsync(FabricItem item, string? format = null, CancellationToken ct = default)
    {
        var url = $"{Fabric}workspaces/{E(item.WorkspaceId)}/items/{E(item.Id)}/getDefinition" + (format is null ? "" : $"?format={E(format)}");
        var json = await LongRunningAsync(HttpMethod.Post, url, null, wantResult: true, ct);
        return json!["definition"]!["parts"]!.AsArray().Select(p => new DefinitionPart(
                Str(p!, "path"), Encoding.UTF8.GetString(Convert.FromBase64String(Str(p!, "payload"))), Str(p!, "payloadType")))
            .ToList();
    }

    /// <summary>The definition as raw bytes per part, for writing to disk (text decoding would corrupt images and custom visuals).</summary>
    public async Task<List<ItemFile>> GetDefinitionFilesAsync(FabricItem item, string? format = null, CancellationToken ct = default)
    {
        var url = $"{Fabric}workspaces/{E(item.WorkspaceId)}/items/{E(item.Id)}/getDefinition" + (format is null ? "" : $"?format={E(format)}");
        var json = await LongRunningAsync(HttpMethod.Post, url, null, wantResult: true, ct);
        return json!["definition"]!["parts"]!.AsArray()
            .Select(p => new ItemFile(Str(p!, "path"), Convert.FromBase64String(Str(p!, "payload")))).ToList();
    }

    /// <summary>Downloads a report as a .pbix (the fallback for reports without an item definition).</summary>
    public async Task<byte[]> ExportReportAsync(FabricItem report, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, $"{PowerBi}groups/{E(report.WorkspaceId)}/reports/{E(report.Id)}/Export", null, Auth.PowerBi, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Whatever the service can say about an item that has no definition: dashboard tiles, dataflow JSON, else the item record.</summary>
    public async Task<JsonElement?> ItemMetadataAsync(FabricItem item, CancellationToken ct = default)
    {
        var url = item.Type switch
        {
            "Dashboard" => $"{PowerBi}groups/{E(item.WorkspaceId)}/dashboards/{E(item.Id)}/tiles",
            "Dataflow" => $"{PowerBi}groups/{E(item.WorkspaceId)}/dataflows/{E(item.Id)}",
            _ => $"{Fabric}workspaces/{E(item.WorkspaceId)}/items/{E(item.Id)}",
        };
        try { return JsonSerializer.Deserialize<JsonElement>((await GetJsonAsync(url, Auth.PowerBi, ct)).ToJsonString()); }
        catch (FabricException) { return null; }
    }

    public Task UpdateDefinitionAsync(FabricItem item, IEnumerable<DefinitionPart> parts, CancellationToken ct = default)
    {
        var body = new
        {
            definition = new
            {
                parts = parts.Select(p => new
                {
                    path = p.Path,
                    payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Payload)),
                    payloadType = "InlineBase64",
                }),
            },
        };
        return LongRunningAsync(HttpMethod.Post, $"{Fabric}workspaces/{E(item.WorkspaceId)}/items/{E(item.Id)}/updateDefinition", body, wantResult: false, ct);
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────

    /// <summary>Starts an on-demand job and returns the job-instance URL to poll.</summary>
    public async Task<string> RunJobAsync(FabricItem item, string jobType, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Post,
            $"{Fabric}workspaces/{E(item.WorkspaceId)}/items/{E(item.Id)}/jobs/instances?jobType={E(jobType)}", null, Auth.PowerBi, ct);
        return res.Headers.Location?.ToString() ?? throw new FabricException(res.StatusCode, "Job started but no instance URL was returned.");
    }

    public async Task<JobState> JobStateAsync(string instanceUrl, CancellationToken ct = default)
    {
        var n = await GetJsonAsync(instanceUrl, Auth.PowerBi, ct);
        return new JobState(Str(n, "status"), n["failureReason"]?["message"]?.GetValue<string>());
    }

    // ── Lakehouses ───────────────────────────────────────────────────────────

    public async Task<LakehouseInfo> LakehouseAsync(FabricItem item, CancellationToken ct = default)
    {
        var p = (await GetJsonAsync($"{Fabric}workspaces/{E(item.WorkspaceId)}/lakehouses/{E(item.Id)}", Auth.PowerBi, ct))["properties"];
        return new LakehouseInfo(
            p?["sqlEndpointProperties"]?["connectionString"]?.GetValue<string>(),
            p?["oneLakeTablesPath"]?.GetValue<string>(),
            p?["oneLakeFilesPath"]?.GetValue<string>());
    }

    public async Task<List<LakehouseTable>> LakehouseTablesAsync(FabricItem item, CancellationToken ct = default) =>
        (await PagedAsync($"{Fabric}workspaces/{E(item.WorkspaceId)}/lakehouses/{E(item.Id)}/tables", ct, "data"))
            .Select(n => new LakehouseTable(Str(n, "name"), Str(n, "type"), Str(n, "format"), Str(n, "location")))
            .ToList();

    /// <summary>Lists one directory level under the lakehouse (e.g. "Files" or "Files/raw").</summary>
    public async Task<List<OneLakeEntry>> ListFilesAsync(FabricItem item, string directory, CancellationToken ct = default)
    {
        var url = $"{OneLake}{E(item.WorkspaceId)}?resource=filesystem&recursive=false&directory={Uri.EscapeDataString($"{item.Id}/{directory}")}";
        var prefix = item.Id + "/";
        var n = await GetJsonAsync(url, Auth.Storage, ct);
        return (n["paths"]?.AsArray() ?? [])
            .Select(p =>
            {
                var full = Str(p!, "name");
                var rel = full.StartsWith(prefix) ? full[prefix.Length..] : full;
                return new OneLakeEntry(rel[(rel.LastIndexOf('/') + 1)..], rel,
                    p!["isDirectory"]?.ToString() == "true",
                    long.TryParse(p["contentLength"]?.ToString(), out var len) ? len : 0,
                    p["lastModified"]?.ToString() ?? "");
            })
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Semantic models (Power BI API) ───────────────────────────────────────

    public async Task<QueryResult> ExecuteQueryAsync(FabricItem model, string dax, CancellationToken ct = default)
    {
        var body = new { queries = new[] { new { query = dax } }, serializerSettings = new { includeNulls = true } };
        using var res = await SendAsync(HttpMethod.Post, $"{PowerBi}groups/{E(model.WorkspaceId)}/datasets/{E(model.Id)}/executeQueries", body, Auth.PowerBi, ct);
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
        var result = json["results"]![0]!;
        if (result["error"] is { } err) throw new FabricException(res.StatusCode, err["message"]?.ToString() ?? err.ToJsonString());
        return ParseRows(result["tables"]![0]!["rows"]!.AsArray());
    }

    internal static QueryResult ParseRows(JsonArray rows)
    {
        var cols = new List<string>();
        foreach (var r in rows)
            foreach (var kv in r!.AsObject())
                if (!cols.Contains(kv.Key)) cols.Add(kv.Key);
        var data = rows.Select(r => cols.Select(c => r![c] is { } v
            ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString()
            : null).ToArray()).ToList();
        return new QueryResult(cols, data);
    }

    /// <summary>Every report in a workspace with the model it reads.</summary>
    public async Task<List<WorkspaceReport>> ReportsAsync(Workspace ws, CancellationToken ct = default) =>
        (await PagedAsync($"{PowerBi}groups/{E(ws.Id)}/reports", ct))
            .Select(n => new WorkspaceReport(new ReportRef(Str(n, "id"), Str(n, "name"), ws.Id, ws.DisplayName, Str(n, "webUrl")),
                Str(n, "datasetId"), n["datasetWorkspaceId"]?.GetValue<string>() ?? ws.Id))
            .ToList();

    /// <summary>Reports in one workspace bound to the given semantic model.</summary>
    public async Task<List<ReportRef>> ReportsForModelAsync(Workspace ws, string modelId, CancellationToken ct = default) =>
        (await ReportsAsync(ws, ct))
            .Where(r => string.Equals(r.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Ref).ToList();

    /// <summary>A model's data sources as the service reports them (type plus server/database, URL or path).</summary>
    public async Task<List<MSource>> ModelSourcesAsync(FabricItem model, CancellationToken ct = default) =>
        (await PagedAsync($"{PowerBi}groups/{E(model.WorkspaceId)}/datasets/{E(model.Id)}/datasources", ct))
            .Select(n =>
            {
                var c = n["connectionDetails"];
                string? P(string k) => c?[k]?.GetValue<string>();
                var target = P("server") is { } server ? (P("database") is { } db ? $"{server}, {db}" : server)
                    : P("url") ?? P("path") ?? P("account") ?? P("domain") ?? "";
                return new MSource(Str(n, "datasourceType"), target);
            })
            .Distinct().ToList();

    /// <summary>Schedule plus recent refreshes (the service keeps roughly the last 60). Null schedule = not refreshable on a schedule.</summary>
    public async Task<RefreshHealth> RefreshHealthAsync(FabricItem model, int top = 30, CancellationToken ct = default)
    {
        var baseUrl = $"{PowerBi}groups/{E(model.WorkspaceId)}/datasets/{E(model.Id)}";
        RefreshSchedule? schedule = null;
        try { schedule = RefreshHealth.ParseSchedule(await GetJsonAsync(baseUrl + "/refreshSchedule", Auth.PowerBi, ct)); }
        catch (FabricException) { } // DirectQuery / Direct Lake / push models have no import schedule
        var runs = (await GetJsonAsync($"{baseUrl}/refreshes?$top={top}", Auth.PowerBi, ct))["value"]?.AsArray()
            .Select(n => RefreshHealth.ParseRun(n!)).ToList() ?? [];
        return new RefreshHealth(schedule, runs);
    }

    public async Task RefreshModelAsync(FabricItem model, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"{PowerBi}groups/{E(model.WorkspaceId)}/datasets/{E(model.Id)}/refreshes",
            new { notifyOption = "NoNotification" }, Auth.PowerBi, ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    async Task<List<JsonNode>> PagedAsync(string url, CancellationToken ct, string arrayProp = "value")
    {
        var all = new List<JsonNode>();
        for (string? next = url; next is not null;)
        {
            var page = await GetJsonAsync(next, Auth.PowerBi, ct);
            all.AddRange(page[arrayProp]?.AsArray().Select(n => n!) ?? []);
            next = page["continuationUri"]?.GetValue<string>();
        }
        return all;
    }

    async Task<JsonNode?> LongRunningAsync(HttpMethod method, string url, object? body, bool wantResult, CancellationToken ct,
        IProgress<int>? progress = null)
    {
        using var res = await SendAsync(method, url, body, Auth.PowerBi, ct);
        if (res.StatusCode != HttpStatusCode.Accepted)
            return res.Content.Headers.ContentLength is 0 ? null : JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));

        var op = res.Headers.Location?.ToString() ?? throw new FabricException(res.StatusCode, "Operation accepted without a status URL.");
        var delay = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            await Task.Delay(delay, ct);
            var state = await GetJsonAsync(op, Auth.PowerBi, ct);
            if (state["percentComplete"]?.GetValueKind() == JsonValueKind.Number) progress?.Report(state["percentComplete"]!.GetValue<int>());
            switch (Str(state, "status"))
            {
                case "Succeeded": return wantResult ? await GetJsonAsync(op + "/result", Auth.PowerBi, ct) : null;
                case "Failed": throw new FabricException(HttpStatusCode.OK, state["error"]?["message"]?.ToString() ?? "Operation failed.",
                    state["error"]?["errorCode"]?.ToString());
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 5));
        }
    }

    async Task<JsonNode> GetJsonAsync(string url, string scope, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, url, null, scope, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, string scope, CancellationToken ct)
    {
        // Never attach a bearer token to anything but Microsoft's Fabric / Power BI endpoints over HTTPS,
        // even when an API hands back a URL to follow (Location, continuationUri, operation status).
        if (!IsTrusted(url, scope)) throw new FabricException(HttpStatusCode.BadRequest, $"Refusing to send credentials to untrusted URL: {url}");
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new("Bearer", await auth.TokenAsync(scope));
            if (scope == Auth.Storage) req.Headers.Add("x-ms-version", "2023-11-03");
            if (body is not null) req.Content = JsonContent.Create(body, options: Json);

            var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return res;
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 << attempt), ct);
                res.Dispose();
                continue;
            }
            var text = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            throw new FabricException(res.StatusCode, ErrorMessage(res.StatusCode, text), ErrorCode(text));
        }
    }

    /// <summary>Hosts allowed to receive a token of the given audience: storage tokens only reach OneLake,
    /// Power BI tokens only the Fabric / Power BI APIs (and their regional *.analysis.windows.net clusters).</summary>
    internal static bool IsTrusted(string url, string scope = Auth.PowerBi)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var h = u.Host;
        return scope == Auth.Storage
            ? h.Equals("onelake.dfs.fabric.microsoft.com", StringComparison.OrdinalIgnoreCase)
            : h.Equals("api.fabric.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
              h.Equals("api.powerbi.com", StringComparison.OrdinalIgnoreCase) ||
              h.EndsWith(".analysis.windows.net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Links the app may open in the browser (portal pages returned by the APIs).</summary>
    public static bool IsPortalLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith(".fabric.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith(".powerbi.com", StringComparison.OrdinalIgnoreCase));

    static string E(string id) => Uri.EscapeDataString(id);

    internal static string ErrorMessage(HttpStatusCode status, string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            var msg = n?["message"] ?? n?["error"]?["message"] ?? n?["error"]?["pbi.error"]?["code"] ?? n?["error"]?["code"];
            if (msg is not null) return $"{(int)status}: {msg}";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { } // non-JSON or unexpected shape
        return $"{(int)status} {status}";
    }

    internal static string? ErrorCode(string body)
    {
        try { return JsonNode.Parse(body)?["errorCode"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    static string Str(JsonNode n, string prop) => n[prop]?.GetValue<string>() ?? "";

    public void Dispose() => _http.Dispose();
}
