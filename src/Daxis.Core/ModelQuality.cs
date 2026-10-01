using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using Token = Daxis.Core.DaxFormatter.Token;
using TokKind = Daxis.Core.DaxFormatter.TokKind;

namespace Daxis.Core;

public enum Severity { Info, Warning, Error }

public enum QualityArea { Architecture, Relationships, Calculations, Performance, Governance }

/// <summary>One object breaking one rule. Suppressed findings carry a BPA ignore annotation and don't count.</summary>
public sealed record Finding(string RuleId, QualityArea Area, Severity Severity, string Rule, string Table, string Object,
    string Detail, string Advice, bool Suppressed = false);

/// <summary>How a rule fared: objects it applied to, how many broke it, and the points it cost its area.</summary>
public sealed record RuleOutcome(string RuleId, QualityArea Area, Severity Severity, string Rule, int Applicable, int Violations, double Penalty);

public sealed record AreaScore(QualityArea Area, double Score, string Grade, int Findings);

public sealed record ComplexityFactor(string Name, string Value, int Points);

/// <summary>How hard the model is to understand and maintain, separate from how well it's built. 0–30.</summary>
public sealed record Complexity(int Index, string Band, IReadOnlyList<ComplexityFactor> Factors)
{
    public IEnumerable<ComplexityFactor> Drivers => Factors.Where(f => f.Points > 0).OrderByDescending(f => f.Points).Take(3);
}

public sealed record QualityReport(double Score, string Grade, string? CapReason, IReadOnlyList<AreaScore> Areas,
    IReadOnlyList<Finding> Findings, IReadOnlyList<RuleOutcome> Rules, Complexity Complexity)
{
    public int Errors => Findings.Count(f => !f.Suppressed && f.Severity == Severity.Error);
    public int Warnings => Findings.Count(f => !f.Suppressed && f.Severity == Severity.Warning);
    public int Infos => Findings.Count(f => !f.Suppressed && f.Severity == Severity.Info);
    public int Suppressed => Findings.Count(f => f.Suppressed);
}

/// <summary>Workspace or estate roll-up: a plain mean of model scores, with the grade spread so a few good models can't hide bad ones.</summary>
public sealed record QualityRollup(int Models, double Score, string Grade, IReadOnlyDictionary<string, int> Grades, int Errors, int Warnings);

/// <summary>
/// Scores a semantic model against best-practice rules drawn from Microsoft's Best Practice Analyzer rule set,
/// Microsoft Learn modelling and DAX guidance, and SQLBI. Reads metadata and storage statistics only; never data.
/// Scoring: per rule, failure rate f = violations / applicable objects (bytes for storage rules); penalty
/// w·min(1, √f) with w = 10/3/1 for error/warning/info; area = 100·(1 − Σpenalty / Σw); overall = weighted area mean,
/// capped at C by any error and at D by three error rules or a broken measure / mismatched key.
/// </summary>
public static class ModelQuality
{
    // ponytail: weights and bands are judgement calls with no published standard; calibrate against real models.
    static readonly Dictionary<QualityArea, double> AreaWeight = new()
    {
        [QualityArea.Performance] = 0.30,
        [QualityArea.Relationships] = 0.20,
        [QualityArea.Calculations] = 0.20,
        [QualityArea.Architecture] = 0.15,
        [QualityArea.Governance] = 0.15,
    };

    static double Weight(Severity s) => s switch { Severity.Error => 10, Severity.Warning => 3, _ => 1 };

    public static string Grade(double score) => score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F";

    public static IReadOnlyList<(string Id, QualityArea Area, Severity Severity, string Title, string Advice)> Catalogue =>
        Rules.Select(r => (r.Id, r.Area, r.Severity, r.Title, r.Advice)).ToList();

    public static QualityReport Score(Model model, StorageInfo? storage = null, StorageStats? stats = null)
    {
        var x = new Ctx(model, storage, stats ?? StorageStats.Empty);
        var findings = new List<Finding>();
        var outcomes = new List<RuleOutcome>();
        foreach (var r in Rules)
        {
            var subjects = r.Scope(x).ToList();
            if (subjects.Count == 0) continue; // not applicable: left out of the denominator
            int hits = 0;
            long hitBytes = 0;
            foreach (var s in subjects)
            {
                if (r.Test(x, s.Obj) is not { } detail) continue;
                var ignored = Ignored(s.Obj, r);
                findings.Add(new Finding(r.Id, r.Area, r.Severity, r.Title, s.Table, s.Name, detail, r.Advice, ignored));
                if (ignored) continue;
                hits++;
                hitBytes += s.Bytes;
            }
            var total = subjects.Sum(s => s.Bytes);
            var f = r.BySize && total > 0 ? (double)hitBytes / total : (double)hits / subjects.Count;
            outcomes.Add(new RuleOutcome(r.Id, r.Area, r.Severity, r.Title, subjects.Count, hits, Weight(r.Severity) * Math.Min(1, Math.Sqrt(f))));
        }

        var areas = outcomes.GroupBy(o => o.Area).OrderBy(g => g.Key).Select(g =>
        {
            var score = 100 * (1 - g.Sum(o => o.Penalty) / g.Sum(o => Weight(o.Severity)));
            return new AreaScore(g.Key, score, Grade(score), findings.Count(f => f.Area == g.Key && !f.Suppressed));
        }).ToList();
        var overall = areas.Count == 0 ? 100 : areas.Sum(a => AreaWeight[a.Area] * a.Score) / areas.Sum(a => AreaWeight[a.Area]);

        var errorRules = outcomes.Where(o => o.Severity == Severity.Error && o.Violations > 0).ToList();
        string? cap = null;
        if (errorRules.Count >= 3 || errorRules.Any(o => o.RuleId is "DAX-01" or "REL-07"))
        {
            cap = errorRules.Count >= 3 ? $"{errorRules.Count} error-level rules broken: capped at D" : $"{errorRules.First(o => o.RuleId is "DAX-01" or "REL-07").Rule}: capped at D";
            overall = Math.Min(overall, 69);
        }
        else if (errorRules.Count > 0)
        {
            cap = $"{errorRules[0].Rule}: capped at C";
            overall = Math.Min(overall, 79);
        }

        var ordered = findings.OrderBy(f => f.Suppressed).ThenByDescending(f => f.Severity).ThenBy(f => f.Area).ThenBy(f => f.RuleId)
            .ThenBy(f => f.Table).ThenBy(f => f.Object).ToList();
        return new QualityReport(overall, Grade(overall), cap, areas, ordered, outcomes, Measure(x));
    }

    public static QualityRollup Rollup(IReadOnlyCollection<QualityReport> reports)
    {
        var mean = reports.Count == 0 ? 0 : reports.Average(r => r.Score);
        var grades = new[] { "A", "B", "C", "D", "F" }.ToDictionary(g => g, g => reports.Count(r => r.Grade == g));
        return new QualityRollup(reports.Count, mean, reports.Count == 0 ? "–" : Grade(mean), grades,
            reports.Sum(r => r.Errors), reports.Sum(r => r.Warnings));
    }

    // ── Complexity ───────────────────────────────────────────────────────────

    static Complexity Measure(Ctx x)
    {
        static int Band(double v, double a, double b, double c) => v <= a ? 0 : v <= b ? 1 : v <= c ? 2 : 3;
        var columns = x.Columns.Count;
        var measures = x.MeasureList;
        var nonTrivial = x.Rels.Count(r => !r.IsActive || r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections || ManyToMany(r));
        var groups = x.Tables.Where(t => t.CalculationGroup is not null).ToList();
        var items = groups.Sum(t => t.CalculationGroup.CalculationItems.Count);
        var lengths = measures.Select(m => (m.Expression ?? "").Length).OrderBy(n => n).ToList();
        var p90 = lengths.Count == 0 ? 0 : lengths[(int)Math.Ceiling(lengths.Count * 0.9) - 1];
        var depth = measures.Select(m => Depth(x.Toks(m.Expression))).DefaultIfEmpty(0).Max();
        var size = x.Storage?.Total ?? 0;
        var modes = x.Tables.SelectMany(t => t.Partitions).Select(Mode).Distinct().Count();
        var rls = x.Model.Roles.SelectMany(r => r.TablePermissions).Where(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression)).ToList();
        var dynamic = rls.Any(tp => Uses(x.Toks(tp.FilterExpression), DynamicFns));
        var composite = modes > 1;
        var mix = (composite && x.Tables.SelectMany(t => t.Partitions).Any(p => Mode(p) == ModeType.DirectQuery)) || (dynamic && x.Rels.Any(ManyToMany)) ? 3
            : composite || dynamic ? 2 : rls.Count > 0 ? 1 : 0;

        var factors = new List<ComplexityFactor>
        {
            new("Tables", $"{x.Tables.Count:N0}", Band(x.Tables.Count, 10, 30, 75)),
            new("Columns", $"{columns:N0}", Band(columns, 150, 500, 1500)),
            new("Measures", $"{measures.Count:N0}", Band(measures.Count, 50, 250, 1000)),
            new("Relationships", $"{x.Rels.Count:N0}", Band(x.Rels.Count, 10, 40, 100)),
            new("Bi-directional, many-to-many or inactive relationships", $"{nonTrivial:N0}", Band(nonTrivial, 0, 3, 10)),
            new("Calculation groups", $"{groups.Count} ({items} items)", groups.Count == 0 ? 0 : groups.Count == 1 || items <= 10 ? 1 : groups.Count <= 4 ? 2 : 3),
            new("Measure length (90th percentile)", $"{p90:N0} characters", Band(p90, 200, 600, 1500)),
            new("Deepest nesting", $"{depth} levels", Band(depth, 3, 6, 10)),
            new("Model size", x.Storage is null ? "unknown" : ModelStorage.Format(size), Band(size / (1024.0 * 1024), 100, 1024, 10240)),
            new("Storage and security mix", (composite ? "composite" : "single mode") + (dynamic ? ", dynamic RLS" : rls.Count > 0 ? ", static RLS" : ""), mix),
        };
        var index = factors.Sum(f => f.Points);
        return new Complexity(index, index <= 7 ? "Low" : index <= 14 ? "Medium" : index <= 21 ? "High" : "Very high", factors);
    }

    // ── Rule plumbing ────────────────────────────────────────────────────────

    sealed record Subject(object Obj, string Table, string Name, long Bytes);

    sealed record Rule(string Id, QualityArea Area, Severity Severity, string Title, string Advice, string? Bpa, bool BySize,
        Func<Ctx, IEnumerable<Subject>> Scope, Func<Ctx, object, string?> Test);

    /// <summary>A rule over objects of type T. Test returns null to pass, or a short detail of the violation.</summary>
    static Rule R<T>(string id, QualityArea area, Severity sev, string title, string advice, Func<Ctx, IEnumerable<T>> scope,
        Func<Ctx, T, string?> test, string? bpa = null, bool bySize = false) where T : notnull =>
        new(id, area, sev, title, advice, bpa, bySize, x => scope(x).Select(o => Subj(x, o)), (x, o) => test(x, (T)o));

    static Subject Subj(Ctx x, object o) => o switch
    {
        Model => new(o, "", "Model", 0),
        Table t => new(o, t.Name, t.Name, 0),
        Column c => new(o, c.Table.Name, c.Name, x.Bytes(c)),
        Measure m => new(o, m.Table.Name, m.Name, 0),
        CalculationItem ci => new(o, ci.CalculationGroup.Table.Name, ci.Name, 0),
        SingleColumnRelationship r => new(o, r.FromTable.Name, Describe(r), 0),
        TablePermission tp => new(o, tp.Table.Name, $"Role '{tp.Role.Name}'", 0),
        _ => new(o, "", o.ToString() ?? "", 0),
    };

    static string Describe(SingleColumnRelationship r) => $"{r.FromTable.Name}[{r.FromColumn.Name}] → {r.ToTable.Name}[{r.ToColumn.Name}]";

    /// <summary>BPA's ignore annotation, on the object or any parent: {"RuleIDs":["AVOID_FLOATING_POINT_DATA_TYPES", ...]}.</summary>
    static bool Ignored(object o, Rule r)
    {
        for (var cur = o; cur is not null; cur = Parent(cur))
        {
            var value = Annotation(cur);
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.TryGetProperty("RuleIDs", out var ids) && ids.ValueKind == JsonValueKind.Array
                    && ids.EnumerateArray().Select(i => i.GetString()).Any(i =>
                        string.Equals(i, r.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(i, r.Bpa, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (JsonException) { } // a malformed annotation suppresses nothing
        }
        return false;

        static string? Annotation(object o)
        {
            const string key = "BestPracticeAnalyzer_IgnoreRules";
            return o switch
            {
                Model m => m.Annotations.Find(key)?.Value, Table t => t.Annotations.Find(key)?.Value,
                Column c => c.Annotations.Find(key)?.Value, Measure m => m.Annotations.Find(key)?.Value,
                Relationship r => r.Annotations.Find(key)?.Value, ModelRole ro => ro.Annotations.Find(key)?.Value,
                TablePermission tp => tp.Annotations.Find(key)?.Value, _ => null,
            };
        }
        static object? Parent(object o) => o switch
        {
            Column c => c.Table, Measure m => m.Table, CalculationItem ci => ci.CalculationGroup.Table,
            Table t => t.Model, Relationship r => r.Model, TablePermission tp => tp.Role, ModelRole ro => ro.Model, _ => null,
        };
    }

    /// <summary>Everything the rules share, computed once per model.</summary>
    sealed class Ctx
    {
        public readonly Model Model;
        public readonly StorageInfo? Storage;
        public readonly StorageStats Stats;
        public readonly List<Table> Tables;
        public readonly List<Column> Columns;
        public readonly List<Measure> MeasureList;
        public readonly Dictionary<string, Measure> Measures;
        public readonly HashSet<string> ColumnNames;
        public readonly List<SingleColumnRelationship> Rels;
        public readonly List<NamedMetadataObject> DaxObjects;
        public readonly bool HasDateTable;
        readonly Dictionary<string, List<Token>> _toks = [];
        HashSet<NamedMetadataObject>? _referenced;
        HashSet<Column>? _attributes;
        Dictionary<string, int>? _bodies;

        public Ctx(Model model, StorageInfo? storage, StorageStats stats)
        {
            Model = model;
            Storage = storage;
            Stats = stats;
            Tables = model.Tables.Where(t => !ModelInsight.IsAutoDateTable(t)).ToList();
            Columns = Tables.SelectMany(t => t.Columns).Where(c => c.Type != ColumnType.RowNumber).ToList();
            MeasureList = Tables.SelectMany(t => t.Measures).ToList();
            Measures = Dax.Measures(model);
            ColumnNames = new(Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            Rels = model.Relationships.OfType<SingleColumnRelationship>().ToList();
            DaxObjects = [.. MeasureList, .. Columns.OfType<CalculatedColumn>(),
                .. Tables.Where(t => t.CalculationGroup is not null).SelectMany(t => t.CalculationGroup.CalculationItems)];
            HasDateTable = Tables.Any(t => t.DataCategory == "Time");
        }

        public bool HasStats => Stats.Rows.Count > 0;
        public long? Rows(Table t) => Stats.Rows.TryGetValue(t.Name, out var n) ? n : null;
        public long? Card(Column c) => Stats.Cardinality.TryGetValue((c.Table.Name, c.Name), out var n) ? n : null;
        public long Bytes(Column c) => Storage?.Of(c.Table.Name, c.Name) ?? 0;

        public List<Token> Toks(string? e)
        {
            if (string.IsNullOrWhiteSpace(e)) return [];
            if (!_toks.TryGetValue(e, out var t)) _toks[e] = t = Dax.Significant(e);
            return t;
        }

        public Table? TableNamed(Token t) => t.Kind switch
        {
            TokKind.SingleQuotedIdent => Model.Tables.Find(t.Text.Trim('\'')),
            TokKind.Identifier => Model.Tables.Find(t.Text),
            _ => null,
        };

        /// <summary>Objects some expression, relationship, sort-by or hierarchy points at.</summary>
        public HashSet<NamedMetadataObject> Referenced => _referenced ??= BuildReferenced();

        HashSet<NamedMetadataObject> BuildReferenced()
        {
            var set = new HashSet<NamedMetadataObject>(ReferenceEqualityComparer.Instance);
            foreach (var o in DaxObjects) set.UnionWith(Dax.Dependencies(Expr(o), Model, Home(o), Measures));
            foreach (var t in Model.Tables)
                if (t.Partitions.FirstOrDefault()?.Source is CalculatedPartitionSource cs) set.UnionWith(Dax.Dependencies(cs.Expression, Model, t, Measures));
            foreach (var tp in Model.Roles.SelectMany(r => r.TablePermissions))
                set.UnionWith(Dax.Dependencies(tp.FilterExpression, Model, tp.Table, Measures));
            set.UnionWith(Attributes);
            return set;
        }

        /// <summary>Columns the engine needs an attribute hierarchy for: relationship keys, sort-by targets, hierarchy levels.</summary>
        public HashSet<Column> Attributes => _attributes ??= [.. Rels.SelectMany(r => new[] { r.FromColumn, r.ToColumn }),
            .. Columns.Where(c => c.SortByColumn is not null).SelectMany(c => new[] { c, c.SortByColumn }),
            .. Model.Tables.SelectMany(t => t.Hierarchies).SelectMany(h => h.Levels).Select(l => l.Column)];

        /// <summary>How many measures share each normalised body.</summary>
        public Dictionary<string, int> Bodies => _bodies ??= MeasureList.GroupBy(m => Normalised(Toks(m.Expression)))
            .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Relationships switched on by USERELATIONSHIP somewhere.</summary>
        public bool Activated(SingleColumnRelationship r) => DaxObjects.Any(o =>
        {
            var t = Toks(Expr(o));
            for (var i = 0; i < t.Count; i++)
                if (Call(t, i, "USERELATIONSHIP"))
                {
                    var args = string.Join(" ", t.Skip(i + 2).Take(Close(t, i + 1) - i - 2).Select(k => k.Text));
                    var deps = Dax.Dependencies(args, Model, Home(o), Measures);
                    if (deps.Contains(r.FromColumn) && deps.Contains(r.ToColumn)) return true;
                }
            return false;
        });

        public bool DynamicRls(Table t) => Model.Roles.SelectMany(r => r.TablePermissions)
            .Any(tp => tp.Table == t && Uses(Toks(tp.FilterExpression), DynamicFns));
    }

    static string? Expr(NamedMetadataObject o) => o switch
    {
        Measure m => m.Expression, CalculatedColumn c => c.Expression, CalculationItem ci => ci.Expression, _ => null,
    };

    static Table? Home(NamedMetadataObject o) => o switch
    {
        Measure m => m.Table, Column c => c.Table, CalculationItem ci => ci.CalculationGroup.Table, _ => null,
    };

    static string? ErrorOf(NamedMetadataObject o) => o switch
    {
        Measure m => m.ErrorMessage, Column c => c.ErrorMessage, CalculationItem ci => ci.ErrorMessage, _ => null,
    };

    static ModeType Mode(Partition p) => p.Mode == ModeType.Default ? p.Table.Model.DefaultMode : p.Mode;

    static bool ManyToMany(SingleColumnRelationship r) =>
        r.FromCardinality == RelationshipEndCardinality.Many && r.ToCardinality == RelationshipEndCardinality.Many;

    static bool IsFieldParameter(Table t) => t.Columns.Any(c => c.ExtendedProperties.Any(p => p.Name == "ParameterMetadata"));

    static bool IsWhatIf(Table t) => t.Partitions.FirstOrDefault()?.Source is CalculatedPartitionSource cs
        && (cs.Expression ?? "").Contains("GENERATESERIES", StringComparison.OrdinalIgnoreCase);

    static bool IsHelper(Table t) => t.CalculationGroup is not null || IsFieldParameter(t) || IsWhatIf(t);

    static bool IsMeasureTable(Table t) => t.Measures.Count > 0 && t.Columns.Where(c => c.Type != ColumnType.RowNumber).All(c => c.IsHidden);

    static bool IsNumeric(Column c) => c.DataType is DataType.Int64 or DataType.Double or DataType.Decimal;

    // ── DAX token helpers (strings and comments are already gone) ────────────

    static readonly string[] DynamicFns = ["USERPRINCIPALNAME", "USERNAME", "USEROBJECTID", "CUSTOMDATA"];
    static readonly string[] Iterators = ["SUMX", "AVERAGEX", "MINX", "MAXX", "COUNTX", "COUNTAX", "PRODUCTX", "CONCATENATEX", "RANKX",
        "FILTER", "ADDCOLUMNS", "SELECTCOLUMNS", "GENERATE", "GENERATEALL"];
    static readonly string[] TimeIntelligence = ["TOTALYTD", "TOTALQTD", "TOTALMTD", "DATESYTD", "DATESQTD", "DATESMTD", "SAMEPERIODLASTYEAR",
        "DATEADD", "PARALLELPERIOD", "PREVIOUSYEAR", "PREVIOUSQUARTER", "PREVIOUSMONTH", "PREVIOUSDAY", "NEXTYEAR", "NEXTQUARTER",
        "NEXTMONTH", "NEXTDAY", "DATESINPERIOD", "OPENINGBALANCEYEAR", "CLOSINGBALANCEYEAR", "STARTOFYEAR", "ENDOFYEAR"];

    static bool Call(List<Token> t, int i, params string[] names) =>
        t[i].Kind == TokKind.Identifier && i + 1 < t.Count && t[i + 1].Kind == TokKind.OpenParen
        && names.Contains(t[i].Text, StringComparer.OrdinalIgnoreCase);

    static bool Uses(List<Token> t, params string[] fns) => Enumerable.Range(0, t.Count).Any(i => Call(t, i, fns));

    static string? FirstUse(List<Token> t, params string[] fns) =>
        Enumerable.Range(0, t.Count).Where(i => Call(t, i, fns)).Select(i => t[i].Text.ToUpperInvariant()).FirstOrDefault();

    /// <summary>Index of the paren closing the one at <paramref name="open"/>.</summary>
    static int Close(List<Token> t, int open)
    {
        for (int i = open, depth = 0; i < t.Count; i++)
        {
            if (t[i].Kind == TokKind.OpenParen) depth++;
            else if (t[i].Kind == TokKind.CloseParen && --depth == 0) return i;
        }
        return t.Count;
    }

    /// <summary>Token span of the first argument of the call whose name is at <paramref name="i"/>.</summary>
    static (int From, int To) FirstArg(List<Token> t, int i)
    {
        var depth = 0;
        for (var j = i + 2; j < t.Count; j++)
        {
            if (t[j].Kind == TokKind.OpenParen) depth++;
            else if (t[j].Kind == TokKind.CloseParen && depth-- == 0) return (i + 2, j - 1);
            else if (t[j].Kind == TokKind.Comma && depth == 0) return (i + 2, j - 1);
        }
        return (i + 2, t.Count - 1);
    }

    static Table? BareTable(Ctx x, List<Token> t, (int From, int To) a) => a.From == a.To && a.From < t.Count ? x.TableNamed(t[a.From]) : null;

    static int Depth(List<Token> t)
    {
        int depth = 0, max = 0;
        foreach (var k in t)
            if (k.Kind == TokKind.OpenParen) max = Math.Max(max, ++depth);
            else if (k.Kind == TokKind.CloseParen) depth--;
        return max;
    }

    static int IteratorDepth(List<Token> t)
    {
        var stack = new Stack<bool>();
        int cur = 0, max = 0;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].Kind == TokKind.OpenParen)
            {
                var it = i > 0 && Call(t, i - 1, Iterators);
                stack.Push(it);
                if (it) max = Math.Max(max, ++cur);
            }
            else if (t[i].Kind == TokKind.CloseParen && stack.Count > 0 && stack.Pop()) cur--;
        }
        return max;
    }

    static string Normalised(List<Token> t) => string.Concat(t.Select(k => k.Text.ToUpperInvariant()));

    static string? FilterTableInCalculate(Ctx x, List<Token> t)
    {
        var stack = new Stack<string>();
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].Kind == TokKind.OpenParen) stack.Push(i > 0 && t[i - 1].Kind == TokKind.Identifier ? t[i - 1].Text.ToUpperInvariant() : "");
            else if (t[i].Kind == TokKind.CloseParen && stack.Count > 0) stack.Pop();
            else if (Call(t, i, "FILTER") && stack.Any(s => s is "CALCULATE" or "CALCULATETABLE"))
            {
                var a = FirstArg(t, i);
                if (BareTable(x, t, a) is { } table) return $"FILTER('{table.Name}', …) as a filter argument";
                if (a.From < t.Count && Call(t, a.From, "ALL")) return "FILTER(ALL(…), …) as a filter argument";
            }
        }
        return null;
    }

    static string? UnqualifiedColumn(Ctx x, List<Token> t)
    {
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].Kind != TokKind.BracketIdent || (i > 0 && x.TableNamed(t[i - 1]) is not null)) continue;
            var name = t[i].Text[1..^1];
            if (!x.Measures.ContainsKey(name) && x.ColumnNames.Contains(name)) return $"[{name}] has no table name";
        }
        return null;
    }

    static string? QualifiedMeasure(Ctx x, List<Token> t)
    {
        for (var i = 1; i < t.Count; i++)
        {
            if (t[i].Kind != TokKind.BracketIdent || x.TableNamed(t[i - 1]) is not { } table) continue;
            var name = t[i].Text[1..^1];
            if (x.Measures.ContainsKey(name) && table.Columns.Find(name) is null) return $"{t[i - 1].Text}[{name}] names a measure with a table";
        }
        return null;
    }

    static string? BareDivide(List<Token> t)
    {
        for (var i = 0; i + 1 < t.Count; i++)
            if (t[i].Kind == TokKind.Operator && t[i].Text == "/" && t[i + 1].Kind != TokKind.Number) return "Uses / with a non-constant denominator";
        return null;
    }

    static string? UnusedVariable(List<Token> t)
    {
        for (var i = 0; i + 1 < t.Count; i++)
        {
            if (t[i].Kind != TokKind.Identifier || !t[i].Text.Equals("VAR", StringComparison.OrdinalIgnoreCase) || t[i + 1].Kind != TokKind.Identifier) continue;
            var name = t[i + 1].Text;
            if (!t.Skip(i + 2).Any(k => k.Kind == TokKind.Identifier && k.Text.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"VAR {name} is never used";
        }
        return null;
    }

    static string? MeasurePerRow(Ctx x, List<Token> t)
    {
        for (var i = 0; i < t.Count; i++)
        {
            if (!Call(t, i, "SUMX", "AVERAGEX", "MINX", "MAXX", "COUNTX", "CONCATENATEX", "RANKX", "FILTER")) continue;
            var a = FirstArg(t, i);
            if (BareTable(x, t, a) is not { } table || x.Rows(table) is not { } rows || rows <= 100_000) continue;
            var end = Close(t, i + 1);
            for (var j = a.To + 1; j < end; j++)
                if (t[j].Kind == TokKind.BracketIdent && x.TableNamed(t[j - 1]) is null && x.Measures.ContainsKey(t[j].Text[1..^1]))
                    return $"{t[i].Text.ToUpperInvariant()} over '{table.Name}' ({rows:N0} rows) calls {t[j].Text} on every row";
        }
        return null;
    }

    static readonly HashSet<string> MonthWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    static bool IsMonthName(string name) => name.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries).Any(MonthWords.Contains);

    // ── The catalogue ────────────────────────────────────────────────────────

    const QualityArea Arch = QualityArea.Architecture, Rel = QualityArea.Relationships, Calc = QualityArea.Calculations,
        Perf = QualityArea.Performance, Gov = QualityArea.Governance;
    const Severity E = Severity.Error, W = Severity.Warning, I = Severity.Info;

    static IEnumerable<Model> Whole(Ctx x) => [x.Model];

    static readonly Rule[] Rules =
    [
        // Architecture
        R("ARC-01", Arch, W, "Auto date/time tables", "Turn off auto date/time and use one marked date table; each hidden date table costs memory.",
            Whole, (x, m) => m.Tables.Count(ModelInsight.IsAutoDateTable) is > 0 and var n ? $"{n} hidden auto date tables" : null, "REMOVE_AUTO-DATE_TABLE"),
        R("ARC-02", Arch, W, "No marked date table", "Mark a dedicated date table so time intelligence behaves predictably.",
            x => x.Tables.Count > 0 ? Whole(x) : [], (x, _) => x.HasDateTable ? null : "No table is marked as a date table", "MODEL_SHOULD_HAVE_A_DATE_TABLE"),
        R("ARC-03", Arch, W, "Date table not marked", "Mark date tables as date tables (Table tools → Mark as date table).",
            x => x.Tables.Where(t => t.Name.Contains("date", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("calendar", StringComparison.OrdinalIgnoreCase)),
            (x, t) => t.DataCategory == "Time" ? null : "Looks like a date table but isn't marked", "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE"),
        R("ARC-04", Arch, I, "Snowflaked dimension", "Flatten dimension chains into one table per dimension (star schema).",
            x => x.Tables, (x, t) => x.Rels.Any(r => r.ToTable == t) && x.Rels.Any(r => r.FromTable == t && r.ToTable != t)
                ? "Filters another dimension while being filtered by a fact" : null, "SNOWFLAKE_SCHEMA_ARCHITECTURE"),
        R("ARC-05", Arch, I, "Disconnected table", "Relate the table to the model, or remove it if it's a leftover.",
            x => x.Tables.Where(t => !IsHelper(t) && !IsMeasureTable(t)),
            (x, t) => x.Rels.Any(r => r.FromTable == t || r.ToTable == t) ? null : "No relationships", "ENSURE_TABLES_HAVE_RELATIONSHIPS"),
        R("ARC-06", Arch, W, "Pivoted month columns", "Unpivot month columns into rows with a date key.",
            x => x.Tables, (x, t) => t.Columns.Count(c => IsMonthName(c.Name)) is >= 3 and var n ? $"{n} columns named after months" : null,
            "UNPIVOT_PIVOTED_(MONTH)_DATA"),
        R("ARC-07", Arch, I, "Calculated table", "Build the table upstream (source or Power Query) so it compresses and refreshes like any other.",
            x => x.Tables.Where(t => !IsHelper(t) && t.DataCategory != "Time"),
            (x, t) => t.Partitions.FirstOrDefault()?.Source is CalculatedPartitionSource ? "Built with DAX at refresh" : null, "REDUCE_USAGE_OF_CALCULATED_TABLES"),
        R("ARC-08", Arch, W, "Many calculated columns", "Move calculated columns upstream; they compress worse and slow refresh.",
            Whole, (x, _) => x.Columns.OfType<CalculatedColumn>().Count() is > 5 and var n ? $"{n} calculated columns" : null, "REDUCE_NUMBER_OF_CALCULATED_COLUMNS"),
        R("ARC-09", Arch, W, "Calculated column uses RELATED", "Denormalise in the source or Power Query instead of RELATED in a calculated column.",
            x => x.Columns.OfType<CalculatedColumn>(), (x, c) => Uses(x.Toks(c.Expression), "RELATED") ? "Uses RELATED" : null,
            "AVOID_USING_CALCULATED_COLUMNS_THAT_USE_THE_RELATED_FUNCTION"),
        R("ARC-10", Arch, I, "DirectQuery without aggregations", "Add aggregation tables so common queries don't hit the source.",
            x => x.Tables.SelectMany(t => t.Partitions).Any(p => Mode(p) == ModeType.DirectQuery) ? Whole(x) : [],
            (x, _) => x.Columns.Any(c => c.AlternateOf is not null) ? null : "DirectQuery tables with no aggregations", "MODEL_USING_DIRECT_QUERY_AND_NO_AGGREGATIONS"),
        R("ARC-11", Arch, I, "Direct Lake falls back silently", "Set Direct Lake behaviour to DirectLakeOnly so fallback to DirectQuery surfaces as an error.",
            x => x.Tables.SelectMany(t => t.Partitions).Any(p => p.Source is EntityPartitionSource && Mode(p) == ModeType.DirectLake) ? Whole(x) : [],
            (x, m) => m.DirectLakeBehavior == DirectLakeBehavior.Automatic ? "Direct Lake behaviour is Automatic" : null),

        // Relationships
        R("REL-01", Rel, W, "Bi-directional relationship", "Use single-direction filtering; enable both directions per measure with CROSSFILTER.",
            x => x.Rels, (x, r) => r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections ? "Filters both directions" : null),
        R("REL-02", Rel, W, "Too many complex relationships", "Keep bi-directional and many-to-many relationships the exception (under 30%).",
            x => x.Rels.Count > 0 ? Whole(x) : [], (x, _) =>
                x.Rels.Count(r => r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections || ManyToMany(r)) is var n && n > 0.3 * x.Rels.Count
                    ? $"{n} of {x.Rels.Count} relationships are bi-directional or many-to-many" : null,
            "AVOID_EXCESSIVE_BI-DIRECTIONAL_OR_MANY-TO-MANY_RELATIONSHIPS"),
        R("REL-03", Rel, W, "Bi-directional on a high-cardinality key", "Bi-directional filtering over a large key is expensive; make it single-direction.",
            x => x.Rels.Where(r => r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections && x.Card(r.ToColumn) is not null),
            (x, r) => x.Card(r.ToColumn) is > 100_000 and var n ? $"Key has {n:N0} distinct values" : null,
            "AVOID_BI-DIRECTIONAL_RELATIONSHIPS_AGAINST_HIGH-CARDINALITY_COLUMNS"),
        R("REL-04", Rel, I, "Many-to-many relationship", "Check the many-to-many is intended; a bridge table is often clearer.",
            x => x.Rels, (x, r) => ManyToMany(r) ? "Many-to-many cardinality" : null),
        R("REL-05", Rel, W, "Many-to-many filtering both ways", "Many-to-many relationships should filter in one direction.",
            x => x.Rels.Where(ManyToMany), (x, r) => r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections ? "Many-to-many and bi-directional" : null,
            "MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION"),
        R("REL-06", Rel, I, "One-to-one relationship", "Merge one-to-one tables into one.",
            x => x.Rels, (x, r) => r.FromCardinality == RelationshipEndCardinality.One && r.ToCardinality == RelationshipEndCardinality.One ? "One-to-one" : null),
        R("REL-07", Rel, E, "Key data types differ", "Relationship columns must share a data type.",
            x => x.Rels, (x, r) => r.FromColumn.DataType != r.ToColumn.DataType ? $"{r.FromColumn.DataType} → {r.ToColumn.DataType}" : null,
            "RELATIONSHIP_COLUMNS_SHOULD_BE_OF_THE_SAME_DATA_TYPE"),
        R("REL-08", Rel, W, "Inactive relationship never used", "Remove inactive relationships no measure activates with USERELATIONSHIP.",
            x => x.Rels.Where(r => !r.IsActive), (x, r) => x.Activated(r) ? null : "No USERELATIONSHIP activates it",
            "INACTIVE_RELATIONSHIPS_THAT_ARE_NEVER_ACTIVATED"),
        R("REL-09", Rel, W, "Security filters both directions", "Apply security filtering in one direction unless the RLS design needs both.",
            x => x.Rels, (x, r) => r.SecurityFilteringBehavior == SecurityFilteringBehavior.BothDirections ? "Security filter applies both directions" : null),
        R("REL-10", Rel, E, "Many-to-many with dynamic RLS", "Avoid many-to-many relationships on tables secured by dynamic row-level security.",
            x => x.Rels.Where(ManyToMany), (x, r) => x.DynamicRls(r.FromTable) || x.DynamicRls(r.ToTable) ? "Joins a table with dynamic RLS" : null,
            "AVOID_USING_MANY-TO-MANY_RELATIONSHIPS_ON_TABLES_USED_FOR_DYNAMIC_ROW_LEVEL_SECURITY"),
        R("REL-11", Rel, E, "USERELATIONSHIP on a secured table", "Don't activate relationships into tables filtered by RLS; the engine can error or bypass filters.",
            x => x.Rels.Where(r => !r.IsActive && x.Activated(r)),
            (x, r) => x.Model.Roles.SelectMany(ro => ro.TablePermissions).Any(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression) && (tp.Table == r.ToTable || tp.Table == r.FromTable))
                ? "Activated with USERELATIONSHIP and the table has RLS" : null,
            "AVOID_THE_USERELATIONSHIP_FUNCTION_AND_RLS_AGAINST_THE_SAME_TABLE"),

        // Calculations
        R("DAX-01", Calc, E, "Expression in error", "Fix the expression; the object returns an error to every visual using it.",
            x => x.DaxObjects, (x, o) => string.IsNullOrWhiteSpace(ErrorOf(o)) ? null : ErrorOf(o)),
        R("DAX-02", Calc, E, "Empty expression", "Give the object an expression or delete it.",
            x => x.DaxObjects, (x, o) => string.IsNullOrWhiteSpace(Expr(o)) ? "No expression" : null, "EXPRESSION_RELIANT_OBJECTS_MUST_HAVE_AN_EXPRESSION"),
        R("DAX-03", Calc, W, "Unqualified column reference", "Always write columns as 'Table'[Column] so they can't be mistaken for measures.",
            x => x.DaxObjects.Where(o => o is not CalculatedColumn), (x, o) => UnqualifiedColumn(x, x.Toks(Expr(o))), "UNQUALIFIED_COLUMN_REFERENCES"),
        R("DAX-04", Calc, W, "Measure reference with a table name", "Write measures as [Measure], without a table name.",
            x => x.DaxObjects, (x, o) => QualifiedMeasure(x, x.Toks(Expr(o))), "MEASURES_SHOULD_NOT_BE_FULLY_QUALIFIED"),
        R("DAX-05", Calc, W, "Division operator", "Use DIVIDE() so a zero or blank denominator returns blank instead of an error.",
            x => x.DaxObjects, (x, o) => BareDivide(x.Toks(Expr(o))), "USE_THE_DIVIDE_FUNCTION_FOR_DIVISION"),
        R("DAX-06", Calc, W, "Error functions", "Avoid IFERROR and ISERROR; guard the cause (e.g. DIVIDE) instead.",
            x => x.DaxObjects, (x, o) => FirstUse(x.Toks(Expr(o)), "IFERROR", "ISERROR") is { } f ? $"Uses {f}" : null, "AVOID_USING_THE_IFERROR_FUNCTION"),
        R("DAX-07", Calc, W, "Table filter in CALCULATE", "Filter columns, not tables: CALCULATE([M], 'T'[Col] = x) or KEEPFILTERS.",
            x => x.DaxObjects, (x, o) => FilterTableInCalculate(x, x.Toks(Expr(o))), "FILTER_COLUMN_VALUES"),
        R("DAX-08", Calc, W, "INTERSECT as a virtual relationship", "Use TREATAS for virtual relationships.",
            x => x.DaxObjects, (x, o) => Uses(x.Toks(Expr(o)), "INTERSECT") ? "Uses INTERSECT" : null, "USE_THE_TREATAS_FUNCTION_INSTEAD_OF_INTERSECT"),
        R("DAX-09", Calc, I, "EARLIER", "Replace EARLIER/EARLIEST with variables.",
            x => x.DaxObjects, (x, o) => FirstUse(x.Toks(Expr(o)), "EARLIER", "EARLIEST") is { } f ? $"Uses {f}" : null),
        R("DAX-10", Calc, W, "Duplicate measure", "Keep one measure per calculation and reference it.",
            x => x.MeasureList, (x, m) => x.Bodies.GetValueOrDefault(Normalised(x.Toks(m.Expression))) is > 1 and var n ? $"Same expression as {n - 1} other measure(s)" : null,
            "AVOID_DUPLICATE_MEASURES"),
        R("DAX-11", Calc, W, "1 - (x / y)", "Write DIVIDE(y - x, y); 1 - (x/y) returns 1 instead of blank when y is blank.",
            x => x.DaxObjects, (x, o) => Normalised(x.Toks(Expr(o))) is var s && s.IndexOf("1-(", StringComparison.Ordinal) is >= 0 and var at && s.IndexOf('/', at) > 0
                ? "Uses 1 - (x / y)" : null, "AVOID_USING_'1-(X/Y)'_SYNTAX"),
        R("DAX-12", Calc, W, "EVALUATEANDLOG left in", "Remove EVALUATEANDLOG; it's for debugging and slows queries.",
            x => x.DaxObjects, (x, o) => Uses(x.Toks(Expr(o)), "EVALUATEANDLOG") ? "Uses EVALUATEANDLOG" : null, "EVALUATEANDLOG_SHOULD_NOT_BE_USED_IN_PRODUCTION_MODELS"),
        R("DAX-13", Calc, W, "Measure without a format string", "Give every visible measure a format string.",
            x => x.MeasureList.Where(m => !m.IsHidden), (x, m) => string.IsNullOrWhiteSpace(m.FormatString) && m.FormatStringDefinition is null ? "No format string" : null,
            "PROVIDE_FORMAT_STRING_FOR_MEASURES"),
        R("DAX-14", Calc, I, "Long or deeply nested expression", "Break long expressions up with variables and helper measures.",
            x => x.DaxObjects, (x, o) => (Expr(o) ?? "").Length is > 1500 and var n ? $"{n:N0} characters"
                : Depth(x.Toks(Expr(o))) is > 10 and var d ? $"Nested {d} levels deep" : null),
        R("DAX-15", Calc, W, "Calculation group without items", "Add calculation items or remove the group.",
            x => x.Tables.Where(t => t.CalculationGroup is not null), (x, t) => t.CalculationGroup.CalculationItems.Count == 0 ? "No calculation items" : null,
            "CALCULATION_GROUPS_WITH_NO_CALCULATION_ITEMS"),
        R("DAX-16", Calc, W, "Measure evaluated per row of a large table", "Iterating a large table and calling a measure per row forces a context transition each row; aggregate first or iterate a smaller table.",
            x => x.HasStats ? x.DaxObjects.Where(o => o is not CalculatedColumn) : [], (x, o) => MeasurePerRow(x, x.Toks(Expr(o)))),
        R("DAX-17", Calc, I, "Unused variable", "Remove variables that are never used.",
            x => x.DaxObjects, (x, o) => UnusedVariable(x.Toks(Expr(o)))),
        R("DAX-18", Calc, W, "Deeply nested iterators", "Three or more nested iterators multiply cost; flatten with variables or SUMMARIZE.",
            x => x.DaxObjects, (x, o) => IteratorDepth(x.Toks(Expr(o))) is >= 3 and var n ? $"{n} iterators nested" : null),
        R("DAX-19", Calc, W, "COUNTROWS(FILTER(...))", "Use CALCULATE(COUNTROWS(T), <column filter>) instead of COUNTROWS(FILTER(T, ...)).",
            x => x.DaxObjects, (x, o) => x.Toks(Expr(o)) is var t && Enumerable.Range(0, t.Count).Any(i => Call(t, i, "COUNTROWS") && i + 2 < t.Count && Call(t, i + 2, "FILTER"))
                ? "COUNTROWS over FILTER" : null),
        R("DAX-20", Calc, W, "Time intelligence without a date table", "Time intelligence functions need a marked date table to return correct results.",
            x => x.HasDateTable ? [] : x.DaxObjects, (x, o) => FirstUse(x.Toks(Expr(o)), TimeIntelligence) is { } f ? $"Uses {f}" : null),
        R("DAX-21", Calc, W, "CALCULATE in a calculated column", "CALCULATE in a calculated column triggers a context transition per row; use RELATED or a column expression.",
            x => x.Columns.OfType<CalculatedColumn>(), (x, c) => Uses(x.Toks(c.Expression), "CALCULATE", "CALCULATETABLE") ? "Uses CALCULATE" : null),

        // Performance and storage
        R("PERF-01", Perf, W, "Floating-point column", "Use Fixed decimal or Whole number instead of Decimal number (Double).",
            x => x.Columns.Where(c => !IsHelper(c.Table)), (x, c) => c.DataType == DataType.Double ? "Double" : null, "AVOID_FLOATING_POINT_DATA_TYPES", bySize: true),
        R("PERF-02", Perf, W, "Date-time with a time part", "Split date and time into separate columns, or drop the time; it explodes the dictionary.",
            x => x.Columns.Where(c => c.DataType == DataType.DateTime && x.Card(c) is not null),
            // ponytail: 10,000 distinct values ≈ 27 years of whole days; more almost certainly means a time component
            (x, c) => x.Card(c) is > 10_000 and var n ? $"{n:N0} distinct values" : null, "SPLIT_DATE_AND_TIME", bySize: true),
        R("PERF-03", Perf, W, "High-cardinality text column", "Remove, shorten or split long text columns with many distinct values.",
            x => x.Columns.Where(c => c.DataType == DataType.String && !IsHelper(c.Table)),
            (x, c) => x.Card(c) is > 1_000_000 and var n ? $"{n:N0} distinct values"
                : x.Storage is { Total: > 0 } s && x.Bytes(c) > s.Total / 10 ? $"{ModelStorage.Format(x.Bytes(c))}, over 10% of the model" : null,
            "REDUCE_USAGE_OF_LONG-LENGTH_COLUMNS_WITH_HIGH_CARDINALITY", bySize: true),
        R("PERF-04", Perf, I, "Column dominates the model", "Look at this column first when reducing model size.",
            x => x.Storage is { Total: > 0 } ? x.Columns : [], (x, c) => x.Bytes(c) is var b && b > x.Storage!.Total / 10 ? $"{(double)b / x.Storage.Total:P0} of the model" : null,
            bySize: true),
        R("PERF-05", Perf, W, "Hidden column nothing uses", "Remove hidden columns no measure, relationship, sort-by or RLS rule uses.",
            x => x.Columns.Where(c => c.IsHidden && !IsHelper(c.Table)), (x, c) => x.Referenced.Contains(c) ? null : x.Bytes(c) is > 0 and var b ? ModelStorage.Format(b) : "Unused",
            "REMOVE_UNNECESSARY_COLUMNS", bySize: true),
        R("PERF-06", Perf, W, "Large table in one partition", "Use incremental refresh (or partitions) on large import tables.",
            x => x.Tables.Where(t => x.Rows(t) is not null && t.Partitions.Count > 0 && t.Partitions.All(p => Mode(p) == ModeType.Import)),
            (x, t) => x.Rows(t) is > 25_000_000 and var n && t.Partitions.Count == 1 && t.RefreshPolicy is null ? $"{n:N0} rows, no refresh policy" : null,
            "LARGE_TABLES_SHOULD_BE_PARTITIONED"),
        R("PERF-07", Perf, I, "Unneeded attribute hierarchy", "Set IsAvailableInMdx to false on hidden columns that aren't keys, sort-by targets or hierarchy levels.",
            x => x.Columns.Where(c => c.IsHidden && !x.Attributes.Contains(c)), (x, c) => c.IsAvailableInMDX ? "IsAvailableInMDX is true" : null,
            "SET_ISAVAILABLEINMDX_TO_FALSE_ON_NON-ATTRIBUTE_COLUMNS", bySize: true),
        R("PERF-08", Perf, W, "String functions in RLS", "Keep RLS filters to simple column comparisons; string functions run on every query.",
            x => x.Model.Roles.SelectMany(r => r.TablePermissions).Where(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression)),
            (x, tp) => FirstUse(x.Toks(tp.FilterExpression), "LEFT", "RIGHT", "MID", "UPPER", "LOWER", "FIND", "SEARCH", "SUBSTITUTE", "CONTAINSSTRING") is { } f
                ? $"Uses {f}" : null, "LIMIT_ROW_LEVEL_SECURITY_(RLS)_LOGIC"),
        R("PERF-09", Perf, I, "Large model", "Over 1 GB needs capacity and the large model format; check size reduction first.",
            x => x.Storage is { Total: > 0 } ? Whole(x) : [], (x, _) => x.Storage!.Total > 1L << 30 ? ModelStorage.Format(x.Storage.Total) : null),

        // Governance and maintainability
        R("GOV-01", Gov, W, "Measure without a description", "Describe visible measures; descriptions show in tooltips, Copilot and data agents.",
            x => x.MeasureList.Where(m => !m.IsHidden), (x, m) => string.IsNullOrWhiteSpace(m.Description) ? "No description" : null, "OBJECTS_WITH_NO_DESCRIPTION"),
        R("GOV-02", Gov, I, "Table or column without a description", "Describe visible tables and columns.",
            x => x.Tables.Where(t => !t.IsHidden).Cast<NamedMetadataObject>().Concat(x.Columns.Where(c => !c.IsHidden && !c.Table.IsHidden)),
            (x, o) => string.IsNullOrWhiteSpace(o switch { Table t => t.Description, Column c => c.Description, _ => "" }) ? "No description" : null,
            "OBJECTS_WITH_NO_DESCRIPTION"),
        R("GOV-03", Gov, E, "Name starts or ends with a space", "Trim object names; spaces break tooling and references.",
            x => x.Tables.Cast<NamedMetadataObject>().Concat(x.Columns).Concat(x.MeasureList),
            (x, o) => o.Name != o.Name.Trim() ? $"'{o.Name}'" : null, "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE"),
        R("GOV-04", Gov, E, "Control characters in a name", "Remove tabs, line breaks and other control characters from names.",
            x => x.Tables.Cast<NamedMetadataObject>().Concat(x.Columns).Concat(x.MeasureList),
            (x, o) => o.Name.Any(char.IsControl) ? "Name contains a control character" : null, "AVOID_INVALID_NAME_CHARACTERS"),
        R("GOV-05", Gov, W, "Hidden measure nothing uses", "Delete hidden measures no other measure references.",
            x => x.MeasureList.Where(m => m.IsHidden), (x, m) => x.Referenced.Contains(m) ? null : "Hidden and unreferenced", "REMOVE_UNNECESSARY_MEASURES"),
        R("GOV-06", Gov, W, "Visible foreign key", "Hide foreign keys; users should filter through the dimension.",
            x => x.Rels.Where(r => r.FromCardinality == RelationshipEndCardinality.Many).Select(r => r.FromColumn).Distinct(),
            (x, c) => c.IsHidden ? null : "Visible", "HIDE_FOREIGN_KEYS"),
        R("GOV-07", Gov, W, "Numeric column summarises by default", "Set Summarize by to None and give users explicit measures.",
            x => x.Columns.Where(c => !c.IsHidden && IsNumeric(c) && !c.Table.IsHidden), (x, c) => c.SummarizeBy == AggregateFunction.None ? null : $"Summarize by {c.SummarizeBy}",
            "DO_NOT_SUMMARIZE_NUMERIC_COLUMNS"),
        R("GOV-08", Gov, W, "Month name sorts alphabetically", "Sort month-name columns by a month-number column.",
            x => x.Columns.Where(c => c.DataType == DataType.String && c.Name.Contains("month", StringComparison.OrdinalIgnoreCase)),
            (x, c) => c.SortByColumn is null ? "No sort-by column" : null, "MONTH_(AS_A_STRING)_MUST_BE_SORTED"),
        R("GOV-09", Gov, I, "Measures without display folders", "Group measures into display folders once a table has more than 20.",
            x => x.Tables.Where(t => t.Measures.Count(m => !m.IsHidden) > 20),
            (x, t) => t.Measures.All(m => string.IsNullOrWhiteSpace(m.DisplayFolder)) ? $"{t.Measures.Count(m => !m.IsHidden)} measures, no folders" : null),
        R("GOV-10", Gov, I, "Dynamic row-level security", "Review dynamic RLS: test each role and keep the filter simple.",
            x => x.Model.Roles.SelectMany(r => r.TablePermissions).Where(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression)),
            (x, tp) => FirstUse(x.Toks(tp.FilterExpression), DynamicFns) is { } f ? $"Filters on {f}()" : null, "CHECK_IF_DYNAMIC_ROW_LEVEL_SECURITY_(RLS)_IS_NECESSARY"),
    ];
}
