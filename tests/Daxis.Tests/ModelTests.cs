using Daxis.Core;
using Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace Daxis.Tests;

public sealed class MQueryTests
{
    const string Orders = """
        let
            // Pull from the warehouse
            Source = Sql.Database("sql.contoso.com", "Sales"),
            dbo_Orders = Source{[Schema="dbo",Item="Orders"]}[Data],
            #"Filtered Rows" = Table.SelectRows(dbo_Orders, each [Amount] > 0 and [Note] <> "a, b = c"),
            Nested = let x = 1, y = 2 in x + y,
            #"Changed Type" = Table.TransformColumnTypes(#"Filtered Rows", {{"Amount", type number}})
        in
            #"Changed Type"
        """;

    [Fact]
    public void Steps_AreTopLevelOnly_AndUnquoted()
    {
        Assert.Equal(["Source", "dbo_Orders", "Filtered Rows", "Nested", "Changed Type"], MQuery.Steps(Orders).Select(s => s.Name));
    }

    [Fact]
    public void Steps_EmptyForNonLetExpressions() => Assert.Empty(MQuery.Steps("Sql.Database(\"a\", \"b\")"));

    [Fact]
    public void Sources_FindsConnectorAndTarget()
    {
        var s = Assert.Single(MQuery.Sources(Orders));
        Assert.Equal("Sql.Database", s.Connector);
        Assert.Equal("\"sql.contoso.com\", \"Sales\"", s.Target);
    }

    [Fact]
    public void Sources_PrefersInnerFileOverWrapper()
    {
        var s = Assert.Single(MQuery.Sources("Excel.Workbook(File.Contents(\"C:\\data\\x.xlsx\"), null, true)"));
        Assert.Equal("File.Contents", s.Connector);
    }

    [Fact]
    public void References_MatchesPlainAndQuotedNames_NotInsideStrings()
    {
        var m = "let S = stg_Orders, T = #\"Dim Date\", U = \"stg_Customers\" in S";
        Assert.Equal(["stg_Orders", "Dim Date"], MQuery.References(m, ["stg_Orders", "Dim Date", "stg_Customers"]));
    }

    [Fact]
    public void Format_OneStepPerLine_KeepsCommentsAndIsIdempotent()
    {
        var once = MQuery.Format("let   Source=Csv.Document(\"a\"),\n// keep me\n  B = Table.PromoteHeaders(Source) in B");
        Assert.Equal("let\n    Source = Csv.Document(\"a\"),\n    // keep me\n    B = Table.PromoteHeaders(Source)\nin\n    B", once.Replace("\r\n", "\n"));
        Assert.Equal(once, MQuery.Format(once));
    }

    [Fact]
    public void Parameter_IsDetected() =>
        Assert.True(MQuery.IsParameter("\"prod\" meta [IsParameterQuery=true, Type=\"Text\"]"));

    [Fact]
    public void Format_KeepsFunctionSignatureAndHeaderComments()
    {
        var m = "// Gets orders for a region\n(region as text) => let Source = Sql.Database(\"s\", \"d\"), R = Table.SelectRows(Source, each [Region] = region) in R";
        var f = MQuery.Format(m).Replace("\r\n", "\n");
        Assert.StartsWith("// Gets orders for a region\n(region as text) =>\nlet\n    Source = ", f);
        Assert.Equal(f, MQuery.Format(f).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Format_NeverPutsASeparatorInsideATrailingComment()
    {
        var f = MQuery.Format("let\n    Source = 1 // note\n    , B = Source\nin\n    B").Replace("\r\n", "\n");
        Assert.Contains("Source = 1 // note\n    ,\n", f);
        Assert.Equal(["Source", "B"], MQuery.Steps(f).Select(s => s.Name));
    }
}

public sealed class ModelInsightTests
{
    static Model BuildModel()
    {
        var db = new Database("M") { CompatibilityLevel = 1604 };
        db.Model = new Model();
        var m = db.Model;
        m.Expressions.Add(new NamedExpression { Name = "Server", Kind = ExpressionKind.M, Expression = "\"sql.contoso.com\" meta [IsParameterQuery=true]" });
        m.Expressions.Add(new NamedExpression { Name = "stg_Orders", Kind = ExpressionKind.M, Expression = "let Source = Sql.Database(Server, \"Sales\"), O = Source{[Item=\"Orders\"]}[Data] in O" });

        var sales = new Table { Name = "Sales" };
        sales.Partitions.Add(new Partition { Name = "Sales", Source = new MPartitionSource { Expression = "let S = stg_Orders, F = Table.SelectRows(S, each true) in F" } });
        sales.Columns.Add(new DataColumn { Name = "Amount", DataType = DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new CalculatedColumn { Name = "Double", Expression = "[Amount] * 2" });
        sales.Measures.Add(new Measure { Name = "Total", Expression = "SUM(Sales[Amount])" });
        m.Tables.Add(sales);

        var calc = new Table { Name = "Calc" };
        calc.Partitions.Add(new Partition { Name = "Calc", Source = new CalculatedPartitionSource { Expression = "{1}" } });
        m.Tables.Add(calc);
        return m;
    }

    [Fact]
    public void Summarize_CountsAndLineageThroughStagingQueries()
    {
        var s = ModelInsight.Summarize(BuildModel());
        Assert.Equal((2, 1, 1), (s.Tables, s.Measures, s.CalcColumns));
        Assert.Equal(2, s.MQueries);               // Sales partition + stg_Orders (parameter excluded)
        Assert.Equal(4, s.Steps);                  // 2 in Sales + 2 in staging
        var sales = s.TableRows.Single(t => t.Name == "Sales");
        Assert.Equal("Sql.Database", sales.Source);
        Assert.Equal("Sql.Database  →  stg_Orders  →  2 steps  →  Import", sales.Lineage);
        Assert.Equal("Calculated table (DAX)", s.TableRows.Single(t => t.Name == "Calc").Kind);
        Assert.Equal(["Parameter", "Staging query"], s.SharedQueries.Select(q => q.Kind));
        Assert.Equal(1, s.SharedQueries.Single(q => q.Name == "stg_Orders").UsedBy);
    }

    [Fact]
    public void ChangeTracker_ReportsModifiedAddedDeletedAndRenamed()
    {
        var m = BuildModel();
        var tracker = new ChangeTracker();
        tracker.Capture(m);
        var sales = m.Tables["Sales"];

        sales.Measures["Total"].Expression = "SUM(Sales[Amount]) + 0";
        sales.Measures["Total"].Name = "Total Sales";
        sales.Columns.Remove(sales.Columns["Double"]);
        sales.Measures.Add(new Measure { Name = "New", Expression = "1" });

        var c = tracker.Diff(m);
        Assert.Contains(c, x => x is { Action: "Modified", Property: "Expression", Path: "Sales / Total", After: "SUM(Sales[Amount]) + 0" });
        Assert.Contains(c, x => x is { Action: "Modified", Property: "Name", Before: "Total", After: "Total Sales" });
        Assert.Contains(c, x => x is { Action: "Deleted", ObjectType: "Calculated column", Before: "[Amount] * 2" });
        Assert.Contains(c, x => x is { Action: "Added", Path: "Sales / New", After: "1" });
        Assert.Equal(4, c.Count);
    }

    [Fact]
    public void ChangeTracker_NoChanges_IsEmpty()
    {
        var m = BuildModel();
        var tracker = new ChangeTracker();
        tracker.Capture(m);
        Assert.Empty(tracker.Diff(m));
    }
}

public sealed class ParameterTests
{
    [Fact]
    public void Sources_ResolveParameterValues()
    {
        var p = new Dictionary<string, string> { ["SqlServer"] = "\"sql-prod\"" };
        Assert.Equal("\"sql-prod\", \"DW\"", MQuery.SubstituteParameters("SqlServer, \"DW\"", p));
        Assert.Equal("\"sql-prod\"", MQuery.ParameterValue("\"sql-prod\" meta [IsParameterQuery=true]"));
    }
}
