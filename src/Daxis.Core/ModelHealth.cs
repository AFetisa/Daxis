using System.Text.Json.Nodes;

namespace Daxis.Core;

// ── Storage (VertiPaq) ───────────────────────────────────────────────────────

public sealed record ColumnStorage(long Dictionary, long Data, long Hierarchy)
{
    public long Total => Dictionary + Data + Hierarchy;
}

/// <summary>In-memory size per column and per table. Direct Lake only counts columns currently paged in.</summary>
public sealed record StorageInfo(IReadOnlyDictionary<(string Table, string Column), ColumnStorage> Columns, IReadOnlyDictionary<string, long> Tables)
{
    public long Total => Tables.Values.Sum();
    public long Dictionaries => Columns.Values.Sum(c => c.Dictionary);
    public long Data => Columns.Values.Sum(c => c.Data);
    public long Hierarchies => Columns.Values.Sum(c => c.Hierarchy);
    /// <summary>Relationship indexes and other table-level structures not owned by a column.</summary>
    public long Other => Math.Max(0, Total - Dictionaries - Data - Hierarchies);
    public (string Table, long Bytes) Largest => Tables.OrderByDescending(t => t.Value).Select(t => (t.Key, t.Value)).FirstOrDefault();
    public long Of(string table, string column) => Columns.GetValueOrDefault((table, column))?.Total ?? 0;
}

public static class ModelStorage
{
    /// <summary>
    /// Max memory per semantic model (GB) by capacity SKU, from Microsoft's published Fabric / Premium limits.
    /// A full refresh needs roughly twice the model's size. Null when the SKU isn't known.
    /// </summary>
    public static double? ModelLimitGb(string? sku) => sku?.ToUpperInvariant() switch
    {
        "F2" or "F4" or "F8" or "A1" or "EM1" => 3,
        "F16" or "A2" or "EM2" => 5,
        "F32" or "A3" or "EM3" => 10,
        "F64" or "FT1" or "P1" or "A4" => 25,
        "F128" or "P2" or "A5" => 50,
        "F256" or "P3" or "A6" or "PP3" => 100,
        "F512" or "P4" or "A7" => 200,
        "F1024" or "F2048" or "P5" or "A8" => 400,
        _ => null,
    };

    /// <summary>
    /// Folds the engine's storage DMVs into per-column sizes, the way VertiPaq Analyzer does:
    /// dictionary (DISCOVER_STORAGE_TABLE_COLUMNS, BASIC_DATA) + data segments + attribute hierarchy ("H$table$column").
    /// Relationship and user-hierarchy structures count toward their table only.
    /// </summary>
    public static StorageInfo Compute(IEnumerable<IReadOnlyDictionary<string, object?>> columns, IEnumerable<IReadOnlyDictionary<string, object?>> segments)
    {
        static string S(IReadOnlyDictionary<string, object?> r, string k) => r.GetValueOrDefault(k)?.ToString() ?? "";
        static long L(IReadOnlyDictionary<string, object?> r, string k) =>
            r.GetValueOrDefault(k) is { } v && long.TryParse(v.ToString(), out var n) ? n : 0;

        // (table, storage column id) → column name + dictionary size
        var ids = new Dictionary<(string, string), (string Name, long Dict)>();
        foreach (var r in columns.Where(r => S(r, "COLUMN_TYPE") == "BASIC_DATA"))
        {
            var name = S(r, "ATTRIBUTE_NAME");
            if (name.StartsWith("RowNumber-", StringComparison.Ordinal)) continue; // internal row id
            ids[(S(r, "DIMENSION_NAME"), S(r, "COLUMN_ID"))] = (name, L(r, "DICTIONARY_SIZE"));
        }

        var data = new Dictionary<(string, string), long>();
        var hier = new Dictionary<(string, string), long>();
        var tables = new Dictionary<string, long>();
        foreach (var r in segments)
        {
            var (table, tableId, colId, used) = (S(r, "DIMENSION_NAME"), S(r, "TABLE_ID"), S(r, "COLUMN_ID"), L(r, "USED_SIZE"));
            tables[table] = tables.GetValueOrDefault(table) + used;
            if (tableId.StartsWith("H$", StringComparison.Ordinal))
            {
                // "H$Sales (15)$Amount (27)" belongs to column id "Amount (27)"
                var key = ids.Keys.FirstOrDefault(k => k.Item1 == table && tableId.EndsWith("$" + k.Item2, StringComparison.Ordinal));
                if (key != default) hier[key] = hier.GetValueOrDefault(key) + used;
            }
            else if (!tableId.Contains('$') && ids.ContainsKey((table, colId)))
                data[(table, colId)] = data.GetValueOrDefault((table, colId)) + used;
        }

        var result = new Dictionary<(string, string), ColumnStorage>();
        foreach (var (key, (name, dict)) in ids)
        {
            result[(key.Item1, name)] = new ColumnStorage(dict, data.GetValueOrDefault(key), hier.GetValueOrDefault(key));
            tables[key.Item1] = tables.GetValueOrDefault(key.Item1) + dict; // dictionaries aren't segments
        }
        return new StorageInfo(result, tables);
    }

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

// ── Refresh health ───────────────────────────────────────────────────────────

public sealed record RefreshSchedule(bool Enabled, IReadOnlyList<string> Days, IReadOnlyList<string> Times, string TimeZone)
{
    public string Text => !Enabled ? "Scheduled refresh is off"
        : Times.Count == 0 ? "Enabled, no times set"
        : $"{(Days.Count is 0 or 7 ? "Daily" : string.Join(", ", Days.Select(d => d[..3])))} at {string.Join(", ", Times)} · {TimeZone}";
}

public static class ScheduleTime
{
    /// <summary>Converts "HH:mm" in the schedule's zone to time of day in <paramref name="to"/> (today's offsets).</summary>
    public static IReadOnlyList<TimeSpan> Starts(RefreshSchedule s, TimeZoneInfo to)
    {
        TimeZoneInfo from;
        try { from = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { from = TimeZoneInfo.Utc; }
        var today = DateTime.UtcNow.Date;
        return s.Times.Select(t => TimeSpan.TryParse(t, out var ts) ? ts : (TimeSpan?)null).OfType<TimeSpan>()
            .Select(ts => TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(today + ts, DateTimeKind.Unspecified), from, to).TimeOfDay)
            .Order().ToList();
    }

    /// <summary>Most scheduled refreshes running at once (each lasting its typical duration), and when that first happens.</summary>
    public static (int Count, TimeSpan At) Peak(IEnumerable<(IReadOnlyList<TimeSpan> Starts, TimeSpan Duration)> models)
    {
        var events = models.SelectMany(m => m.Starts.SelectMany(st =>
        {
            var end = st + (m.Duration > TimeSpan.Zero ? m.Duration : TimeSpan.FromMinutes(1));
            // wrap refreshes that run past midnight
            return end <= TimeSpan.FromDays(1)
                ? new[] { (st, 1), (end, -1) }
                : new[] { (st, 1), (TimeSpan.FromDays(1), -1), (TimeSpan.Zero, 1), (end - TimeSpan.FromDays(1), -1) };
        })).OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToList();
        int now = 0, best = 0;
        var at = TimeSpan.Zero;
        foreach (var (t, d) in events)
        {
            now += d;
            if (now > best) { best = now; at = t; }
        }
        return (best, at);
    }
}

public sealed record RefreshRun(DateTimeOffset Start, DateTimeOffset? End, string Status, string Type, string? Error)
{
    public TimeSpan? Duration => End - Start;
    public bool Failed => Status == "Failed";
}

public sealed record RefreshHealth(RefreshSchedule? Schedule, IReadOnlyList<RefreshRun> Runs)
{
    IEnumerable<TimeSpan> Done => Runs.Where(r => r.Status == "Completed" && r.Duration is not null).Select(r => r.Duration!.Value);
    public RefreshRun? Last => Runs.FirstOrDefault();
    public TimeSpan? Typical => Done.Order().ToList() is { Count: > 0 } d ? d[d.Count / 2] : null;
    public TimeSpan? Slowest => Done.DefaultIfEmpty().Max() is var m && m > TimeSpan.Zero ? m : null;
    public double? SuccessRate => Runs.Count(r => r.Status is "Completed" or "Failed") is > 0 and var n ? (double)Runs.Count(r => r.Status == "Completed") / n : null;
    public string? LastError => Runs.FirstOrDefault(r => r.Failed)?.Error;

    public string LastText => Last is not { } l ? "Never refreshed"
        : l.Status == "Unknown" ? $"Running since {l.Start.ToLocalTime():d MMM HH:mm}"
        : $"{l.Status} {Ago(l.End ?? l.Start)}" + (l.Duration is { } d ? $" · took {Dur(d)}" : "");
    public string TypicalText => Typical is { } t ? Dur(t) : "–";
    public string SlowestText => Slowest is { } s ? Dur(s) : "–";
    public string SuccessText => SuccessRate is { } r ? $"{r:P0}" : "–";

    public static string Dur(TimeSpan t) => t.TotalMinutes >= 60 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalSeconds >= 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.TotalSeconds:0} s";

    public static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.UtcNow - at;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : $"{(int)d.TotalDays} d ago";
    }

    internal static RefreshSchedule? ParseSchedule(JsonNode? n) => n is null ? null : new(
        n["enabled"]?.GetValue<bool>() ?? false,
        n["days"]?.AsArray().Select(d => d!.GetValue<string>()).ToList() ?? [],
        n["times"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? [],
        n["localTimeZoneId"]?.GetValue<string>() ?? "UTC");

    /// <summary>
    /// The service reports failures in two shapes: {"errorCode","errorDescription"} and
    /// {"error":{"code","pbi.error":{"details":[...]}}}. Prefer a description; otherwise explain the code.
    /// </summary>
    internal static string? FriendlyError(JsonNode? e)
    {
        if (e is null) return null;
        if (e["errorDescription"]?.GetValue<string>() is { Length: > 0 } d) return d;
        var inner = e["error"];
        var detail = inner?["pbi.error"]?["details"]?.AsArray()
            .Select(x => x?["detail"]?["value"]?.GetValue<string>()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var code = e["errorCode"]?.GetValue<string>() ?? inner?["code"]?.GetValue<string>() ?? inner?["pbi.error"]?["code"]?.GetValue<string>();
        if (code is null) return detail;
        var text = Explain(code);
        return detail is null ? $"{text} ({code})" : $"{text}: {detail} ({code})";
    }

    /// <summary>Plain-language meaning of common refresh failure codes; unknown codes are split into words.</summary>
    public static string Explain(string code)
    {
        var c = code.ToLowerInvariant();
        if (c.Contains("timeout")) return c.Contains("gateway") ? "The gateway timed out connecting to the data source" : "The refresh timed out";
        if (c.Contains("credential") || c.Contains("unauthorized") || c.Contains("oauth")) return "Data source credentials are invalid or expired";
        if (c.Contains("gatewaynotreachable") || c.Contains("gatewayoffline") || c.Contains("gateway_unreachable")) return "The gateway is offline or unreachable";
        if (c.Contains("memory")) return "The refresh ran out of memory on the capacity";
        if (c.Contains("capacity") && c.Contains("throttl")) return "The capacity is throttling refreshes";
        if (c.Contains("datasourcenotfound") || c.Contains("notfound")) return "A data source or object wasn't found";
        var words = System.Text.RegularExpressions.Regex.Replace(code.Replace("DM_GWPipeline_", "").Replace('_', ' '), "(?<=[a-z])([A-Z])", " $1").Trim();
        return words.Length > 0 ? char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant() : code;
    }

    internal static RefreshRun ParseRun(JsonNode n)
    {
        static DateTimeOffset? T(JsonNode? v) => DateTimeOffset.TryParse(v?.GetValue<string>(), out var t) ? t : null;
        string? error = null;
        if (n["serviceExceptionJson"]?.GetValue<string>() is { Length: > 0 } ex)
        {
            try { error = FriendlyError(JsonNode.Parse(ex)) ?? ex; }
            catch (System.Text.Json.JsonException) { error = ex; }
        }
        return new RefreshRun(T(n["startTime"]) ?? DateTimeOffset.MinValue, T(n["endTime"]),
            n["status"]?.GetValue<string>() ?? "Unknown", n["refreshType"]?.GetValue<string>() ?? "", error);
    }
}
