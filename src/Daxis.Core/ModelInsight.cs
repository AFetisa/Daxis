using Microsoft.AnalysisServices.Tabular;

namespace Daxis.Core;

public sealed record TableInsight(
    string Name, string Kind, string Mode, string Source, string Lineage,
    int Steps, int Measures, int CalcColumns, int Columns, int Partitions, DateTime? Refreshed = null, long? Bytes = null)
{
    public string SizeText => Bytes is { } b ? ModelStorage.Format(b) : "";
    /// <summary>Oldest partition refresh: a table is only as fresh as its stalest partition.</summary>
    public string RefreshedText => Refreshed is { } r ? RefreshHealth.Ago(new DateTimeOffset(DateTime.SpecifyKind(r, DateTimeKind.Utc))) : "never";
}

public sealed record SharedQueryInsight(string Name, string Kind, int Steps, string Source, int UsedBy);

public sealed record SourceInsight(string Connector, string Target, int Tables);

public sealed record ModelSummary(
    int Tables, int Measures, int CalcColumns, int Columns, int Relationships, int MQueries, int Steps,
    IReadOnlyList<TableInsight> TableRows, IReadOnlyList<SharedQueryInsight> SharedQueries, IReadOnlyList<SourceInsight> Sources);

public sealed record ModelChange(string Action, string ObjectType, string Path, string Property, string? Before, string? After);

public static class ModelInsight
{
    /// <summary>Auto date/time tables Power BI Desktop generates; noise in every overview.</summary>
    public static bool IsAutoDateTable(Table t) =>
        t.Name.StartsWith("DateTableTemplate_") || t.Name.StartsWith("LocalDateTable_");

    public static ModelSummary Summarize(Model model, StorageInfo? storage = null)
    {
        var shared = model.Expressions.Where(e => e.Kind == ExpressionKind.M).ToList();
        var names = shared.Select(e => e.Name).ToList();
        var refs = shared.ToDictionary(e => e.Name, e => MQuery.References(e.Expression, names.Where(n => n != e.Name)));
        var parameters = shared.Where(e => MQuery.IsParameter(e.Expression))
            .ToDictionary(e => e.Name, e => MQuery.ParameterValue(e.Expression));

        // Sources reachable from an expression through staging queries (cycle-safe).
        List<MSource> Resolve(string? m, IEnumerable<string> direct, HashSet<string>? seen = null)
        {
            seen ??= [];
            var result = MQuery.Sources(m).Select(s => s with { Target = MQuery.SubstituteParameters(s.Target, parameters) }).ToList();
            foreach (var r in direct)
                if (seen.Add(r) && shared.FirstOrDefault(e => e.Name == r) is { } q)
                    result.AddRange(Resolve(q.Expression, refs[r], seen));
            return result.Distinct().ToList();
        }

        var tables = model.Tables.Where(t => !IsAutoDateTable(t)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<TableInsight>();
        var usage = new Dictionary<MSource, HashSet<string>>();
        var queryUse = names.ToDictionary(n => n, _ => 0);
        var mQueries = 0;
        var totalSteps = 0;

        foreach (var t in tables)
        {
            var steps = 0;
            var direct = new List<string>();
            var sources = new List<MSource>();
            string kind = "", detail = "";
            foreach (var p in t.Partitions)
            {
                switch (p.Source)
                {
                    case MPartitionSource ms:
                        kind = "Power Query";
                        mQueries++;
                        steps += MQuery.Steps(ms.Expression).Count;
                        var r = MQuery.References(ms.Expression, names);
                        direct.AddRange(r);
                        sources.AddRange(Resolve(ms.Expression, r));
                        break;
                    case CalculatedPartitionSource:
                        kind = "Calculated table (DAX)";
                        break;
                    case EntityPartitionSource es:
                        kind = "Direct Lake";
                        detail = es.EntityName;
                        if (es.ExpressionSource is { } src)
                        {
                            direct.Add(src.Name);
                            sources.AddRange(Resolve(src.Expression, refs.GetValueOrDefault(src.Name) ?? []));
                        }
                        break;
                    case QueryPartitionSource qs:
                        kind = "Native query";
                        detail = qs.DataSource?.Name ?? "";
                        break;
                    default:
                        kind = p.SourceType.ToString();
                        break;
                }
            }
            direct = direct.Distinct().ToList();
            sources = sources.Distinct().ToList();
            foreach (var d in direct) queryUse[d] = queryUse.GetValueOrDefault(d) + 1;
            foreach (var s in sources) (usage.TryGetValue(s, out var set) ? set : usage[s] = []).Add(t.Name);
            totalSteps += steps;

            var mode = t.Partitions.FirstOrDefault()?.Mode is { } m && m != ModeType.Default ? m : model.DefaultMode;
            var sourceText = sources.Count > 0 ? string.Join(", ", sources.Select(s => s.Connector)) : detail;
            rows.Add(new TableInsight(
                t.Name, kind, mode.ToString(), sourceText, Lineage(sources, direct, refs, steps, kind, mode.ToString()),
                steps, t.Measures.Count, t.Columns.Count(c => c is CalculatedColumn),
                t.Columns.Count(c => c.Type != ColumnType.RowNumber), t.Partitions.Count,
                t.Partitions.Select(x => x.RefreshedTime).Where(x => x > new DateTime(2000, 1, 1)).DefaultIfEmpty().Min() is var fresh && fresh != default ? fresh : null,
                storage?.Tables.GetValueOrDefault(t.Name)));
        }

        var sharedRows = shared.Select(e =>
        {
            var kind = MQuery.IsParameter(e.Expression) ? "Parameter"
                : model.Tables.SelectMany(t => t.Partitions).Any(p => p.Source is EntityPartitionSource { ExpressionSource: var x } && x == e) ? "Direct Lake source"
                : "Staging query";
            var steps = MQuery.Steps(e.Expression).Count;
            if (kind != "Parameter") { mQueries++; totalSteps += steps; }
            var usedBy = queryUse.GetValueOrDefault(e.Name) + refs.Count(kv => kv.Value.Contains(e.Name));
            return new SharedQueryInsight(e.Name, kind, steps,
                string.Join(", ", Resolve(e.Expression, refs[e.Name]).Select(s => s.Target.Length > 0 ? $"{s.Connector}({s.Target})" : s.Connector)),
                usedBy);
        }).OrderBy(q => q.Kind).ThenBy(q => q.Name).ToList();

        return new ModelSummary(
            tables.Count,
            tables.Sum(t => t.Measures.Count),
            tables.Sum(t => t.Columns.Count(c => c is CalculatedColumn)),
            tables.Sum(t => t.Columns.Count(c => c.Type != ColumnType.RowNumber)),
            model.Relationships.Count,
            mQueries, totalSteps, rows, sharedRows,
            usage.Select(kv => new SourceInsight(kv.Key.Connector, kv.Key.Target, kv.Value.Count))
                .OrderByDescending(s => s.Tables).ThenBy(s => s.Connector).ToList());
    }

    // "Sql.Database → stg_Sales → 6 steps → Import"
    static string Lineage(List<MSource> sources, List<string> direct, Dictionary<string, IReadOnlyList<string>> refs,
        int steps, string kind, string mode)
    {
        var parts = new List<string>();
        if (sources.Count > 0) parts.Add(string.Join(" + ", sources.Select(s => s.Connector).Distinct()));
        parts.AddRange(direct);
        if (steps > 0) parts.Add($"{steps} step{(steps == 1 ? "" : "s")}");
        if (kind == "Calculated table (DAX)") parts.Add("DAX");
        parts.Add(mode);
        return string.Join("  →  ", parts);
    }
}

/// <summary>Remembers the last saved state so pending edits can be reviewed before they reach the service.</summary>
public sealed class ChangeTracker
{
    sealed record Snap(string Type, string Path, Dictionary<string, string?> Props);

    Dictionary<MetadataObject, Snap> _base = new(ReferenceEqualityComparer.Instance);

    public void Capture(Model model) => _base = Collect(model);

    public List<ModelChange> Diff(Model model)
    {
        var now = Collect(model);
        var changes = new List<ModelChange>();
        foreach (var (obj, before) in _base)
            if (!now.ContainsKey(obj))
                changes.Add(new("Deleted", before.Type, before.Path, before.Props.ContainsKey("Expression") ? "Expression" : "Name",
                    before.Props.GetValueOrDefault("Expression") ?? before.Props["Name"], null));
        foreach (var (obj, after) in now)
        {
            if (!_base.TryGetValue(obj, out var before))
            {
                changes.Add(new("Added", after.Type, after.Path, "Expression", null, after.Props.GetValueOrDefault("Expression") ?? ""));
                continue;
            }
            foreach (var (prop, value) in after.Props)
            {
                var old = before.Props.GetValueOrDefault(prop);
                if ((old ?? "") != (value ?? ""))
                    changes.Add(new("Modified", after.Type, before.Path, prop, old, value));
            }
        }
        return changes.OrderBy(c => c.Path).ThenBy(c => c.Property).ToList();
    }

    static Dictionary<MetadataObject, Snap> Collect(Model model)
    {
        var map = new Dictionary<MetadataObject, Snap>(ReferenceEqualityComparer.Instance);
        foreach (var t in model.Tables)
        {
            map[t] = new("Table", t.Name, new() { ["Name"] = t.Name });
            foreach (var m in t.Measures)
                map[m] = new("Measure", $"{t.Name} / {m.Name}", new()
                {
                    ["Name"] = m.Name, ["Expression"] = m.Expression, ["FormatString"] = m.FormatString,
                    ["DisplayFolder"] = m.DisplayFolder, ["Description"] = m.Description, ["IsHidden"] = m.IsHidden.ToString(),
                });
            foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
            {
                var props = new Dictionary<string, string?>
                {
                    ["Name"] = c.Name, ["FormatString"] = c.FormatString, ["DisplayFolder"] = c.DisplayFolder,
                    ["Description"] = c.Description, ["IsHidden"] = c.IsHidden.ToString(),
                };
                if (c is CalculatedColumn cc) props["Expression"] = cc.Expression;
                map[c] = new(c is CalculatedColumn ? "Calculated column" : "Column", $"{t.Name} / {c.Name}", props);
            }
            foreach (var p in t.Partitions)
            {
                var expr = p.Source switch
                {
                    MPartitionSource ms => ms.Expression,
                    CalculatedPartitionSource cs => cs.Expression,
                    _ => null,
                };
                map[p] = new(p.Source is CalculatedPartitionSource ? "DAX table" : "Power Query", $"{t.Name} / {p.Name}",
                    new() { ["Name"] = p.Name, ["Expression"] = expr });
            }
        }
        foreach (var e in model.Expressions)
            map[e] = new("Shared query", e.Name, new() { ["Name"] = e.Name, ["Expression"] = e.Expression });
        return map;
    }
}
