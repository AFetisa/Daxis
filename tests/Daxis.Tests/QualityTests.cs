using Daxis.Core;
using Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace Daxis.Tests;

public sealed class QualityTests
{
    /// <summary>A small, well-built star: Sales → Product and Sales → Date, everything described, keys hidden.</summary>
    static Model Clean()
    {
        var db = new Database("M") { CompatibilityLevel = 1604 };
        db.Model = new Model();
        var m = db.Model;
        Table T(string name)
        {
            var t = new Table { Name = name, Description = name };
            t.Partitions.Add(new Partition { Name = name, Source = new MPartitionSource { Expression = "let S = 1 in S" } });
            m.Tables.Add(t);
            return t;
        }
        DataColumn C(Table t, string name, DataType type, bool hidden = false)
        {
            var c = new DataColumn { Name = name, DataType = type, SourceColumn = name, IsHidden = hidden, Description = name, SummarizeBy = AggregateFunction.None };
            t.Columns.Add(c);
            return c;
        }

        var sales = T("Sales");
        C(sales, "ProductKey", DataType.Int64, hidden: true);
        C(sales, "DateKey", DataType.DateTime, hidden: true);
        C(sales, "Amount", DataType.Decimal, hidden: true).IsAvailableInMDX = false;
        sales.Measures.Add(new Measure { Name = "Total", Expression = "SUM ( Sales[Amount] )", FormatString = "#,0", Description = "Sales amount" });
        sales.Measures.Add(new Measure { Name = "Average", Expression = "DIVIDE ( [Total], COUNTROWS ( Sales ) )", FormatString = "#,0", Description = "Per sale" });

        var product = T("Product");
        C(product, "ProductKey", DataType.Int64, hidden: true);
        C(product, "Colour", DataType.String);

        var date = T("Date");
        date.DataCategory = "Time";
        C(date, "Date", DataType.DateTime).IsKey = true;
        C(date, "Year", DataType.Int64);

        m.Relationships.Add(new SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductKey"], ToColumn = product.Columns["ProductKey"] });
        m.Relationships.Add(new SingleColumnRelationship { Name = "r2", FromColumn = sales.Columns["DateKey"], ToColumn = date.Columns["Date"] });
        return m;
    }

    static IEnumerable<string> Ids(QualityReport r) => r.Findings.Where(f => !f.Suppressed).Select(f => f.RuleId).Distinct();

    [Fact]
    public void CleanStar_ScoresAWithNoFindings()
    {
        var r = ModelQuality.Score(Clean());
        Assert.Empty(r.Findings);
        Assert.Equal(("A", 100.0), (r.Grade, r.Score));
        Assert.Null(r.CapReason);
        Assert.Equal("Low", r.Complexity.Band);
    }

    [Fact]
    public void Relationships_BidiAndKeyMismatch_CapAtD()
    {
        var m = Clean();
        var rel = (SingleColumnRelationship)m.Relationships["r1"];
        rel.CrossFilteringBehavior = CrossFilteringBehavior.BothDirections;
        m.Tables["Product"].Columns["ProductKey"].DataType = DataType.String;

        var r = ModelQuality.Score(m);
        Assert.Contains("REL-01", Ids(r));
        Assert.Contains("REL-07", Ids(r));
        Assert.Equal("D", r.Grade);
        Assert.Contains("capped at D", r.CapReason);
        Assert.Equal(QualityArea.Relationships, r.Areas.OrderBy(a => a.Score).First().Area);
    }

    [Fact]
    public void Dax_FlagsRealPatternsButNotCommentsOrStrings()
    {
        var m = Clean();
        var sales = m.Tables["Sales"];
        sales.Measures.Add(new Measure { Name = "Bad", FormatString = "0", Description = "x", Expression = """
            VAR unused = 1
            RETURN CALCULATE ( [Total] / [Average], FILTER ( ALL ( Sales ), [Amount] > 0 ) )
            """ });
        sales.Measures.Add(new Measure { Name = "Commented", FormatString = "0", Description = "x",
            Expression = "// IFERROR(1/0) here is only a comment\n\"FILTER(ALL(x))\" & SUM ( Sales[Amount] )" });

        var r = ModelQuality.Score(m);
        var bad = r.Findings.Where(f => f.Object == "Bad").Select(f => f.RuleId).ToList();
        Assert.Contains("DAX-03", bad); // [Amount] unqualified
        Assert.Contains("DAX-05", bad); // bare divide
        Assert.Contains("DAX-07", bad); // FILTER(ALL()) in CALCULATE
        Assert.Contains("DAX-17", bad); // unused VAR
        Assert.DoesNotContain(r.Findings, f => f.Object == "Commented");
    }

    [Fact]
    public void Storage_SizeWeightsColumnRules()
    {
        var m = Clean();
        m.Tables["Product"].Columns.Add(new DataColumn { Name = "Weight", DataType = DataType.Double, SourceColumn = "Weight", IsHidden = true, SummarizeBy = AggregateFunction.None });
        m.Tables["Product"].Columns["Colour"].SortByColumn = m.Tables["Product"].Columns["Weight"]; // used, so only PERF-01 fires
        var bytes = new Dictionary<(string, string), ColumnStorage>
        {
            [("Product", "Weight")] = new(0, 900, 0),
            [("Sales", "Amount")] = new(0, 100, 0),
        };
        var storage = new StorageInfo(bytes, new Dictionary<string, long> { ["Product"] = 900, ["Sales"] = 100 });

        var perf = ModelQuality.Score(m, storage).Rules.Single(o => o.RuleId == "PERF-01");
        // 1 of 9 columns, but 90% of the bytes: penalty 3·√0.9
        Assert.Equal(3 * Math.Sqrt(0.9), perf.Penalty, 6);
    }

    [Fact]
    public void BpaIgnoreAnnotation_SuppressesWithoutScoring()
    {
        var m = Clean();
        var total = m.Tables["Sales"].Measures["Total"];
        total.Description = "";
        total.Annotations.Add(new Annotation { Name = "BestPracticeAnalyzer_IgnoreRules", Value = """{"RuleIDs":["OBJECTS_WITH_NO_DESCRIPTION"]}""" });

        var r = ModelQuality.Score(m);
        Assert.Single(r.Findings, f => f.RuleId == "GOV-01" && f.Suppressed);
        Assert.Equal(1, r.Suppressed);
        Assert.Equal(100.0, r.Score);
    }

    [Fact]
    public void StorageStats_ReadsRowsAndCardinalityFromHierarchyTables()
    {
        static IReadOnlyDictionary<string, object?> Row(params (string K, object V)[] kv) =>
            kv.ToDictionary(p => p.K, p => (object?)p.V, StringComparer.OrdinalIgnoreCase);
        var columns = new[] { Row(("DIMENSION_NAME", "Sales"), ("COLUMN_ID", "Amount (27)"), ("ATTRIBUTE_NAME", "Amount"), ("COLUMN_TYPE", "BASIC_DATA")) };
        var tables = new[]
        {
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "Sales (15)"), ("ROWS_COUNT", 600L)),
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "Sales (15)"), ("ROWS_COUNT", 400L)),
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "H$Sales (15)$Amount (27)"), ("ROWS_COUNT", 53L)),
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "R$Sales (15)$x"), ("ROWS_COUNT", 9L)),
        };
        var s = ModelStorage.Stats(columns, tables);
        Assert.Equal(1000, s.Rows["Sales"]);
        Assert.Equal(50, s.Cardinality[("Sales", "Amount")]);
    }

    [Fact]
    public void Rollup_IsPlainMeanWithGradeSpread()
    {
        var good = ModelQuality.Score(Clean());
        var m = Clean();
        ((SingleColumnRelationship)m.Relationships["r1"]).ToColumn.DataType = DataType.String;
        var bad = ModelQuality.Score(m);

        var r = ModelQuality.Rollup([good, bad]);
        Assert.Equal((good.Score + bad.Score) / 2, r.Score, 6);
        Assert.Equal((1, 1), (r.Grades["A"], r.Grades["D"]));
    }

    [Fact]
    public void Export_CsvQuotesAndJsonUsesNames()
    {
        var m = Clean();
        m.Tables["Sales"].Measures["Total"].Description = "";
        var models = new List<ScoredModel> { new("Finance, prod", "Sales", ModelQuality.Score(m)) };

        var findings = QualityExport.FindingsCsv(models).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, findings.Length);
        Assert.StartsWith("\"Finance, prod\",Sales,GOV-01,Governance,Warning", findings[1]);
        Assert.Contains("\"Severity\": \"Warning\"", QualityExport.Json(models));
        Assert.Contains("GOV-01", QualityExport.Html(models));
        Assert.Contains("GOV-01", QualityExport.Markdown(models));
    }
}
