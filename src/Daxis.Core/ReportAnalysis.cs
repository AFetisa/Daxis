using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AnalysisServices.Tabular;

namespace Daxis.Core;

/// <summary>A model field a report binds to: Entity/Property in the report's query JSON.</summary>
public sealed record FieldRef(string Table, string Name, bool IsMeasure);

public sealed record ReportStats(
    int Pages, int Visuals, int Slicers, int Filters,
    IReadOnlyDictionary<string, int> VisualTypes, IReadOnlySet<FieldRef> Fields);

public enum FieldUse { Report, Model, Unused }

public sealed record FieldUsage(string Table, string Name, string Kind, FieldUse Use, int Reports, bool IsHidden, long? Bytes = null)
{
    public string Size => Bytes is { } b ? ModelStorage.Format(b) : "";
    public string Status => Use switch
    {
        FieldUse.Report => "Used in reports",
        FieldUse.Model => "Model only",
        _ => "Unused",
    };
}

public sealed record UsageCoverage(IReadOnlyList<FieldUsage> Fields, int Missing)
{
    public int Total => Fields.Count;
    public int InReports => Fields.Count(f => f.Use == FieldUse.Report);
    public int ModelOnly => Fields.Count(f => f.Use == FieldUse.Model);
    public int Unused => Fields.Count(f => f.Use == FieldUse.Unused);
    public bool HasSizes => Fields.Any(f => f.Bytes is not null);
    public long TotalBytes => Fields.Sum(f => f.Bytes ?? 0);
    public long UnusedBytes => Fields.Where(f => f.Use == FieldUse.Unused).Sum(f => f.Bytes ?? 0);
}

/// <summary>Reads report definitions (PBIR and PBIR-Legacy) and measures how much of a model the reports use.</summary>
public static class ReportAnalysis
{
    public static ReportStats Analyse(IEnumerable<DefinitionPart> parts)
    {
        int pages = 0, visuals = 0, slicers = 0, filters = 0;
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fields = new HashSet<FieldRef>();

        void Visual(string? type)
        {
            if (type is null) return; // visual groups carry no visualType
            visuals++;
            types[type] = types.GetValueOrDefault(type) + 1;
            if (type.Contains("slicer", StringComparison.OrdinalIgnoreCase)) slicers++;
        }

        foreach (var p in parts)
        {
            var path = p.Path.Replace('\\', '/');
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(p.Payload); }
            catch (JsonException) { continue; }
            if (root is null) continue;
            var file = path[(path.LastIndexOf('/') + 1)..];

            if (file == "report.json" && root["sections"] is JsonArray sections)
            {
                // PBIR-Legacy: one report.json; config and filters are JSON encoded as strings.
                filters += Applied(Embedded(root["filters"]));
                foreach (var s in sections)
                {
                    pages++;
                    filters += Applied(Embedded(s?["filters"]));
                    foreach (var vc in s?["visualContainers"]?.AsArray() ?? [])
                    {
                        Visual(Str(Embedded(vc?["config"])?["singleVisual"]?["visualType"]));
                        filters += Applied(Embedded(vc?["filters"]));
                    }
                }
            }
            else
            {
                // PBIR: report.json, pages/*/page.json, pages/*/visuals/*/visual.json
                if (file == "page.json") pages++;
                if (file == "visual.json") Visual(Str(root["visual"]?["visualType"]));
                filters += Applied(root["filterConfig"]?["filters"]);
            }
            Collect(root, fields, []);
        }
        return new ReportStats(pages, visuals, slicers, filters, types, fields);
    }

    /// <summary>Filter cards with a condition set; Power BI also lists every visual field as an empty card.</summary>
    static int Applied(JsonNode? filters) => filters is JsonArray a ? a.Count(f => f?["filter"] is not null) : 0;

    static JsonNode? Embedded(JsonNode? n)
    {
        if (Str(n) is not { } s) return n;
        try { return JsonNode.Parse(s); }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Walks any JSON, including JSON-in-strings, for Column / Measure / hierarchy references.
    // Query blocks name tables through aliases declared in a sibling "From" array.
    static void Collect(JsonNode? n, HashSet<FieldRef> into, Dictionary<string, string> aliases)
    {
        switch (n)
        {
            case JsonArray a:
                foreach (var x in a) Collect(x, into, aliases);
                break;
            case JsonObject o:
                if (o["From"] is JsonArray from)
                {
                    aliases = new(aliases);
                    foreach (var f in from)
                        if (Str(f?["Name"]) is { } alias && Str(f?["Entity"]) is { } entity) aliases[alias] = entity;
                }
                foreach (var (key, child) in o)
                {
                    switch (key)
                    {
                        case "Column" or "Measure" or "PropertyVariationSource"
                            when Str(child?["Property"]) is { } prop && Entity(child!["Expression"], aliases) is { } t:
                            into.Add(new(t, prop, key == "Measure"));
                            break;
                        case "HierarchyLevel" when Str(child?["Level"]) is { } level
                            && Entity(child!["Expression"]?["Hierarchy"]?["Expression"], aliases) is { } ht:
                            into.Add(new(ht, level, false)); // resolved to its column via the model's hierarchies
                            break;
                    }
                    Collect(child, into, aliases);
                }
                break;
            case JsonValue v when Str(v) is { Length: > 1 } s && (s[0] == '{' || s[0] == '['):
                Collect(Embedded(v), into, aliases);
                break;
        }
    }

    static string? Entity(JsonNode? expr, Dictionary<string, string> aliases)
    {
        var r = expr?["SourceRef"];
        return Str(r?["Entity"]) ?? (Str(r?["Source"]) is { } s ? aliases.GetValueOrDefault(s) : null);
    }

    // ── Coverage ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies every column and measure: bound by a report, needed by the model (a used measure's dependencies,
    /// relationship keys, sort-by columns, calculated tables, RLS), or unused.
    /// </summary>
    public static UsageCoverage Coverage(Model model, IReadOnlyList<IReadOnlySet<FieldRef>> reports, StorageInfo? storage = null)
    {
        var tables = model.Tables.Where(t => !ModelInsight.IsAutoDateTable(t)).ToList();
        var measures = Dax.Measures(model);

        NamedMetadataObject? Resolve(FieldRef f)
        {
            if (f.IsMeasure) return measures.GetValueOrDefault(f.Name);
            var t = model.Tables.Find(f.Table);
            if (t is null) return null;
            return t.Columns.Find(f.Name) as NamedMetadataObject
                ?? t.Hierarchies.SelectMany(h => h.Levels).FirstOrDefault(l => l.Name == f.Name)?.Column as NamedMetadataObject
                ?? measures.GetValueOrDefault(f.Name); // report may reference a measure through a column wrapper
        }

        var reportCount = new Dictionary<NamedMetadataObject, int>(ReferenceEqualityComparer.Instance);
        var missing = new HashSet<FieldRef>();
        foreach (var fields in reports)
        {
            var hit = new HashSet<NamedMetadataObject>(ReferenceEqualityComparer.Instance);
            foreach (var f in fields)
                if (Resolve(f) is { } o) hit.Add(o);
                else missing.Add(f);
            foreach (var o in hit) reportCount[o] = reportCount.GetValueOrDefault(o) + 1;
        }

        // Structural seeds the model needs regardless of reports.
        var seeds = new List<NamedMetadataObject>();
        foreach (var r in model.Relationships.OfType<SingleColumnRelationship>()) seeds.AddRange([r.FromColumn, r.ToColumn]);
        foreach (var t in model.Tables)
            if (t.Partitions.FirstOrDefault()?.Source is CalculatedPartitionSource cs) seeds.AddRange(Dax.Dependencies(cs.Expression, model, t, measures));
        foreach (var role in model.Roles)
            foreach (var tp in role.TablePermissions)
                seeds.AddRange(Dax.Dependencies(tp.FilterExpression, model, tp.Table, measures));

        var structural = Closure(seeds, model, measures);
        var viaReports = Closure(reportCount.Keys, model, measures);

        var rows = new List<FieldUsage>();
        foreach (var t in tables)
        {
            foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                rows.Add(Row(t, c, c is CalculatedColumn ? "Calculated column" : "Column", c.IsHidden));
            foreach (var m in t.Measures) rows.Add(Row(t, m, "Measure", m.IsHidden));
        }
        return new UsageCoverage(rows.OrderBy(r => r.Use).ThenByDescending(r => r.Bytes ?? 0).ThenBy(r => r.Table).ThenBy(r => r.Name).ToList(), missing.Count);

        FieldUsage Row(Table t, NamedMetadataObject o, string kind, bool hidden)
        {
            var n = reportCount.GetValueOrDefault(o);
            var use = n > 0 ? FieldUse.Report : viaReports.Contains(o) || structural.Contains(o) ? FieldUse.Model : FieldUse.Unused;
            return new FieldUsage(t.Name, o.Name, kind, use, n, hidden, o is Column && storage is not null ? storage.Of(t.Name, o.Name) : null);
        }
    }

    static HashSet<NamedMetadataObject> Closure(IEnumerable<NamedMetadataObject> start, Model model, Dictionary<string, Measure> measures)
    {
        var seen = new HashSet<NamedMetadataObject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<NamedMetadataObject>(start);
        while (queue.TryDequeue(out var o))
        {
            if (!seen.Add(o)) continue;
            IEnumerable<NamedMetadataObject> next = o switch
            {
                Measure m => Dax.Dependencies(m.Expression, model, m.Table, measures),
                CalculatedColumn cc => Dax.Dependencies(cc.Expression, model, cc.Table, measures),
                _ => [],
            };
            if (o is Column { SortByColumn: { } sort }) next = next.Append(sort);
            foreach (var n in next) queue.Enqueue(n);
        }
        return seen;
    }
}

/// <summary>Token-level DAX reference finder (no full parse): 'Table'[Col], Table[Col], [Measure], and bare tables.</summary>
public static class Dax
{
    /// <summary>Measure names are unique across a model (case-insensitive).</summary>
    public static Dictionary<string, Measure> Measures(Model model) =>
        model.Tables.SelectMany(t => t.Measures).GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public static List<NamedMetadataObject> Dependencies(string? expression, Model model, Table? home, Dictionary<string, Measure>? measures = null)
    {
        var result = new List<NamedMetadataObject>();
        if (string.IsNullOrWhiteSpace(expression)) return result;
        var toks = Significant(expression);
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != DaxFormatter.TokKind.BracketIdent) continue;
            var name = t.Text.Trim('[', ']');
            var prev = i > 0 ? toks[i - 1] : null;
            var table = prev?.Kind switch
            {
                DaxFormatter.TokKind.SingleQuotedIdent => model.Tables.Find(prev.Text.Trim('\'')),
                DaxFormatter.TokKind.Identifier => model.Tables.Find(prev.Text),
                _ => null,
            };
            if ((table ?? home)?.Columns.Find(name) is { } col) result.Add(col);
            else if ((measures ??= Measures(model)).GetValueOrDefault(name) is { } m)
                result.Add(m);
        }
        return result;
    }

    /// <summary>Tables an expression reads, for calculated-table lineage. A name followed by "(" is a function (DATE, not 'Date').</summary>
    public static List<Table> Tables(string? expression, Model model)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];
        var toks = Significant(expression);
        return toks.Select((t, i) => t.Kind switch
            {
                DaxFormatter.TokKind.SingleQuotedIdent => model.Tables.Find(t.Text.Trim('\'')),
                DaxFormatter.TokKind.Identifier when i + 1 >= toks.Count || toks[i + 1].Kind != DaxFormatter.TokKind.OpenParen
                    => model.Tables.Find(t.Text),
                _ => null,
            })
            .OfType<Table>().Distinct().ToList();
    }

    internal static List<DaxFormatter.Token> Significant(string expression) => DaxFormatter.Tokenize(expression)
        .Where(t => t.Kind is not (DaxFormatter.TokKind.Whitespace or DaxFormatter.TokKind.NewLine
            or DaxFormatter.TokKind.LineComment or DaxFormatter.TokKind.BlockComment))
        .ToList();
}
