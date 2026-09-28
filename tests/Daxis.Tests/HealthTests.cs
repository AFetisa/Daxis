using System.Text.Json.Nodes;
using Daxis.Core;
using Xunit;

namespace Daxis.Tests;

public sealed class HealthTests
{
    static IReadOnlyDictionary<string, object?> Row(params (string K, object? V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void Storage_SumsDictionaryDataAndHierarchy_PerColumn()
    {
        var cols = new[]
        {
            Row(("DIMENSION_NAME", "Sales"), ("ATTRIBUTE_NAME", "Amount"), ("COLUMN_ID", "Amount (27)"), ("COLUMN_TYPE", "BASIC_DATA"), ("DICTIONARY_SIZE", 1000L)),
            Row(("DIMENSION_NAME", "Sales"), ("ATTRIBUTE_NAME", "RowNumber-2662979B"), ("COLUMN_ID", "RowNumber (1)"), ("COLUMN_TYPE", "BASIC_DATA"), ("DICTIONARY_SIZE", 64L)),
            Row(("DIMENSION_NAME", "Sales"), ("ATTRIBUTE_NAME", "Amount"), ("COLUMN_ID", "Amount (27)"), ("COLUMN_TYPE", "RELATIONSHIP"), ("DICTIONARY_SIZE", 999L)),
        };
        var segs = new[]
        {
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "Sales (15)"), ("COLUMN_ID", "Amount (27)"), ("USED_SIZE", 400UL)),
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "Sales (15)"), ("COLUMN_ID", "Amount (27)"), ("USED_SIZE", 100UL)),
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "H$Sales (15)$Amount (27)"), ("COLUMN_ID", "POS_TO_ID"), ("USED_SIZE", 50L)),
            Row(("DIMENSION_NAME", "Sales"), ("TABLE_ID", "R$Sales (15)$abc"), ("COLUMN_ID", "x"), ("USED_SIZE", 7L)),
        };
        var s = ModelStorage.Compute(cols, segs);
        Assert.Equal(new ColumnStorage(1000, 500, 50), s.Columns[("Sales", "Amount")]);
        Assert.Single(s.Columns);                        // row number and non-data columns ignored
        Assert.Equal(1000 + 500 + 50 + 7, s.Tables["Sales"]); // relationship structure counts toward the table
        Assert.Equal("1.5 KB", ModelStorage.Format(1557));
    }

    [Fact]
    public void Refresh_ParsesScheduleRunsAndStats()
    {
        var sched = RefreshHealth.ParseSchedule(JsonNode.Parse("""{"days":["Monday","Tuesday"],"times":["06:00"],"enabled":true,"localTimeZoneId":"AUS Eastern Standard Time"}"""));
        Assert.Equal("Mon, Tue at 06:00 · AUS Eastern Standard Time", sched!.Text);

        RefreshRun Run(string status, int minutes, string? ex = null) => RefreshHealth.ParseRun(JsonNode.Parse(
            new JsonObject { ["status"] = status, ["refreshType"] = "Scheduled", ["startTime"] = "2026-09-28T01:00:00Z",
                ["endTime"] = $"2026-09-28T01:{minutes:00}:00Z", ["serviceExceptionJson"] = ex }.ToJsonString())!);
        var h = new RefreshHealth(sched, [Run("Completed", 4), Run("Failed", 1, """{"errorCode":"ModelRefreshFailed","errorDescription":"Credentials expired"}"""), Run("Completed", 10), Run("Completed", 6)]);
        Assert.Equal(TimeSpan.FromMinutes(6), h.Typical);
        Assert.Equal(TimeSpan.FromMinutes(10), h.Slowest);
        Assert.Equal(0.75, h.SuccessRate);
        Assert.Equal("Credentials expired", h.LastError);
        Assert.Equal("4 min 0 s", RefreshHealth.Dur(TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void Refresh_ExplainsNestedGatewayErrors()
    {
        var e = JsonNode.Parse("""{"error":{"code":"DM_GWPipeline_Gateway_AdoNetProviderOpenConnectionTimeoutError","pbi.error":{"code":"DM_GWPipeline_Gateway_AdoNetProviderOpenConnectionTimeoutError","parameters":{},"details":[],"exceptionCulprit":1}}}""");
        Assert.Equal("The gateway timed out connecting to the data source (DM_GWPipeline_Gateway_AdoNetProviderOpenConnectionTimeoutError)", RefreshHealth.FriendlyError(e));
        Assert.Equal("Some new failure kind", RefreshHealth.Explain("SomeNewFailureKind"));
    }

    [Fact]
    public void Schedule_PeakFindsOverlaps_IncludingPastMidnight()
    {
        var (count, at) = ScheduleTime.Peak([
            ([TimeSpan.FromHours(6)], TimeSpan.FromMinutes(30)),
            ([TimeSpan.FromHours(6.25)], TimeSpan.FromMinutes(30)),
            ([TimeSpan.FromHours(23.75)], TimeSpan.FromMinutes(60)),
            ([TimeSpan.FromHours(0.25)], TimeSpan.FromMinutes(10)),
        ]);
        Assert.Equal((2, TimeSpan.FromHours(0.25)), (count, at)); // 23:45 run spills over and meets 00:15
        var s = new RefreshSchedule(true, [], ["06:00", "18:30"], "UTC");
        Assert.Equal([TimeSpan.FromHours(6), TimeSpan.FromHours(18.5)], ScheduleTime.Starts(s, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Treemap_AreasProportional_InsideBounds_NoOverlap()
    {
        double[] v = [60, 25, 10, 4, 1, 0];
        var b = TreemapLayout.Squarify(v, new Box(0, 0, 400, 250));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(v[i] / 100 * 400 * 250, b[i].W * b[i].H, 3);
            Assert.True(b[i].X >= -1e-9 && b[i].Y >= -1e-9 && b[i].X + b[i].W <= 400 + 1e-6 && b[i].Y + b[i].H <= 250 + 1e-6);
            for (var j = i + 1; j < 5; j++)
            {
                var ox = Math.Min(b[i].X + b[i].W, b[j].X + b[j].W) - Math.Max(b[i].X, b[j].X);
                var oy = Math.Min(b[i].Y + b[i].H, b[j].Y + b[j].H) - Math.Max(b[i].Y, b[j].Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"tiles {i} and {j} overlap");
            }
        }
        Assert.Equal(default, b[5]); // zero value gets no tile
    }

    [Fact]
    public void Capacity_ModelLimits()
    {
        Assert.Equal(25, ModelStorage.ModelLimitGb("F64"));
        Assert.Equal(25, ModelStorage.ModelLimitGb("p1"));
        Assert.Equal(3, ModelStorage.ModelLimitGb("F2"));
        Assert.Null(ModelStorage.ModelLimitGb("PPU-ish"));
    }
}
