using Daxis.Core;
using Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace Daxis.Tests;

public sealed class ReportTests
{
    static DefinitionPart J(string path, string json) => new(path, json, "InlineBase64");

    [Fact]
    public void Pbir_CountsPagesVisualsSlicersAppliedFilters_AndFields()
    {
        var s = ReportAnalysis.Analyse([
            J("definition/report.json", """{"filterConfig":{"filters":[{"name":"f1","field":{"Column":{"Expression":{"SourceRef":{"Entity":"Date"}},"Property":"Year"}},"filter":{"Version":2}}]}}"""),
            J("definition/pages/p1/page.json", """{"name":"p1","filterConfig":{"filters":[{"name":"f2","field":{"Measure":{"Expression":{"SourceRef":{"Entity":"Sales"}},"Property":"Total"}}}]}}"""),
            J("definition/pages/p1/visuals/v1/visual.json", """{"visual":{"visualType":"slicer","query":{"queryState":{"Values":{"projections":[{"field":{"Column":{"Expression":{"SourceRef":{"Entity":"Product"}},"Property":"Colour"}}}]}}}}}"""),
            J("definition/pages/p1/visuals/v2/visual.json", """{"visual":{"visualType":"barChart","query":{"queryState":{"Y":{"projections":[{"field":{"Aggregation":{"Expression":{"Column":{"Expression":{"SourceRef":{"Entity":"Sales"}},"Property":"Amount"}},"Function":0}}}]}}}}}"""),
            J("definition/pages/p1/visuals/g1/visual.json", """{"visualGroup":{"displayName":"Group"}}"""),
            J("definition.pbir", "not json"),
        ]);
        Assert.Equal((1, 2, 1, 1), (s.Pages, s.Visuals, s.Slicers, s.Filters)); // f2 has no condition, so it isn't "applied"
        Assert.Equal(
            new[] { new FieldRef("Date", "Year", false), new FieldRef("Product", "Colour", false), new FieldRef("Sales", "Amount", false), new FieldRef("Sales", "Total", true) },
            s.Fields.OrderBy(f => f.Table).ThenBy(f => f.Name));
    }

    [Fact]
    public void Legacy_ResolvesAliasesInsideEmbeddedJson()
    {
        const string config = """{"singleVisual":{"visualType":"tableEx","prototypeQuery":{"From":[{"Name":"s","Entity":"Sales"}],"Select":[{"Measure":{"Expression":{"SourceRef":{"Source":"s"}},"Property":"Total"}}]}}}""";
        const string filters = """[{"expression":{"Column":{"Expression":{"SourceRef":{"Entity":"Sales"}},"Property":"Amount"}},"filter":{"Version":2}}]""";
        var report = new System.Text.Json.Nodes.JsonObject
        {
            ["sections"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["visualContainers"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["config"] = config, ["filters"] = filters }),
            }),
        };
        var s = ReportAnalysis.Analyse([J("report.json", report.ToJsonString())]);
        Assert.Equal((1, 1, 0, 1), (s.Pages, s.Visuals, s.Slicers, s.Filters));
        Assert.Contains(new FieldRef("Sales", "Total", true), s.Fields);
        Assert.Contains(new FieldRef("Sales", "Amount", false), s.Fields);
    }

    static Model StarModel()
    {
        var db = new Database("M") { CompatibilityLevel = 1604 };
        db.Model = new Model();
        var m = db.Model;
        m.Expressions.Add(new NamedExpression { Name = "stg", Kind = ExpressionKind.M, Expression = "let S = Sql.Database(\"srv\", \"DW\") in S" });
        Table T(string name, string m_, params string[] cols)
        {
            var t = new Table { Name = name };
            t.Partitions.Add(new Partition { Name = name, Source = new MPartitionSource { Expression = m_ } });
            foreach (var c in cols) t.Columns.Add(new DataColumn { Name = c, DataType = DataType.String, SourceColumn = c });
            m.Tables.Add(t);
            return t;
        }
        var sales = T("Sales", "let S = stg in S", "Amount", "ProductKey", "Unused");
        sales.Measures.Add(new Measure { Name = "Total", Expression = "SUM ( Sales[Amount] )" });
        sales.Measures.Add(new Measure { Name = "Orphan", Expression = "1" });
        var product = T("Product", "let S = stg in S", "ProductKey", "Colour", "Name");
        product.Columns["Name"].SortByColumn = product.Columns["Colour"];
        m.Relationships.Add(new SingleColumnRelationship { FromColumn = sales.Columns["ProductKey"], ToColumn = product.Columns["ProductKey"] });
        return m;
    }

    [Fact]
    public void Coverage_SplitsReportModelAndUnused()
    {
        var m = StarModel();
        var cov = ReportAnalysis.Coverage(m, [new HashSet<FieldRef> { new("Sales", "Total", true), new("Product", "Name", false), new("Gone", "X", false) }]);
        FieldUse Use(string n) => cov.Fields.Single(f => f.Name == n && (n != "ProductKey" || f.Table == "Sales")).Use;
        Assert.Equal(FieldUse.Report, Use("Total"));
        Assert.Equal(FieldUse.Report, Use("Name"));
        Assert.Equal(FieldUse.Model, Use("Amount"));     // via the Total measure
        Assert.Equal(FieldUse.Model, Use("Colour"));     // sort-by of a used column
        Assert.Equal(FieldUse.Model, Use("ProductKey")); // relationship key
        Assert.Equal(FieldUse.Unused, Use("Unused"));
        Assert.Equal(FieldUse.Unused, Use("Orphan"));
        Assert.Equal(1, cov.Missing);
    }

    [Fact]
    public void Coverage_CarriesColumnSizes_AndTotalsUnusedBytes()
    {
        var storage = new StorageInfo(new Dictionary<(string, string), ColumnStorage>
        {
            [("Sales", "Unused")] = new(700, 300, 0), [("Sales", "Amount")] = new(10, 10, 0),
        }, new Dictionary<string, long> { ["Sales"] = 1020 });
        var cov = ReportAnalysis.Coverage(StarModel(), [new HashSet<FieldRef> { new("Sales", "Total", true) }], storage);
        Assert.Equal(1000, cov.Fields.Single(f => f.Name == "Unused").Bytes);
        Assert.Null(cov.Fields.Single(f => f.Name == "Total").Bytes); // measures take no storage
        Assert.Equal(1000, cov.UnusedBytes);
        Assert.Equal("Unused", cov.Fields.First(f => f.Use == FieldUse.Unused).Name); // biggest unused first
    }

    [Fact]
    public void Workspace_SourcesThenModelsThenReports_IncludingExternalModels()
    {
        FabricItem M(string id, string name) => new(id, name, "SemanticModel", null, "w1");
        WorkspaceReport R(string id, string model, string ws = "w1") => new(new ReportRef(id, "Report " + id, "w1", "WS", ""), model, ws);
        var g = ModelGraph.Workspace([M("m1", "Sales"), M("m2", "Finance")],
            new Dictionary<string, IReadOnlyList<MSource>> { ["m1"] = [new("Sql", "srv, DW")], ["m2"] = [new("Sql", "srv, DW"), new("Web", "https://x")] },
            [R("r1", "m1"), R("r2", "m1"), R("r3", "mX", "w9")], (w, m) => $"{w}/{m}");
        GNode N(string title) => g.Nodes.Single(n => n.Title == title);
        Assert.Equal(2, g.Edges.Count(e => e.From == N("srv, DW")));            // one shared source card feeds both models
        Assert.True(N("srv, DW").Layer < N("Sales").Layer && N("Sales").Layer < N("Report r1").Layer);
        Assert.Equal("Semantic model · external", N("Model in another workspace").Kicker);
        Assert.Equal("w9/mX", N("Model in another workspace").Link);
        Assert.Equal("1 source · 2 reports", N("Sales").Detail);
    }

    [Fact]
    public void DaxTables_IgnoresFunctionsNamedLikeTables()
    {
        var m = StarModel();
        m.Tables.Add(new Table { Name = "Date" });
        var t = Dax.Tables("ADDCOLUMNS ( 'Product', \"D\", DATE ( 2026, 1, 1 ) ) // Sales", m);
        Assert.Equal(["Product"], t.Select(x => x.Name));
    }

    static bool Overlap(GNode a, GNode b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    [Fact]
    public void Lineage_FlowsLeftToRight_WithoutOverlaps()
    {
        var g = ModelGraph.Lineage(StarModel(), [("Sales report", null, new HashSet<FieldRef> { new("Sales", "Total", true) })]);
        GNode N(string title) => g.Nodes.Single(n => n.Title == title);
        Assert.True(N("srv, DW").Layer < N("stg").Layer);
        Assert.True(N("stg").Layer < N("Sales").Layer);
        Assert.Equal(N("Sales").Layer, N("Product").Layer);
        Assert.True(N("Sales").Layer < N("Sales report").Layer);
        Assert.Contains(g.Edges, e => e.From.Title == "Sales" && e.To.Title == "Sales report");
        Assert.DoesNotContain(g.Edges, e => e.From.Title == "Product" && e.To.Title == "Sales report");
        Assert.All(g.Edges, e => Assert.True(e.From.X < e.To.X));
        Assert.DoesNotContain(g.Nodes.SelectMany((a, i) => g.Nodes.Skip(i + 1).Select(b => (a, b))), p => Overlap(p.a, p.b));
    }

    [Fact]
    public void Diagram_RelationshipEdgesAnchorOnKeyRows_WithoutOverlaps()
    {
        var g = ModelGraph.Diagram(StarModel());
        var e = Assert.Single(g.Edges);
        Assert.Equal(("Product", "Sales", "1", "*"), (e.From.Title, e.To.Title, e.FromLabel, e.ToLabel));
        Assert.Equal("ProductKey", e.From.Rows[e.FromRow].Text);
        Assert.False(Overlap(g.Nodes[0], g.Nodes[1]));
    }
}
