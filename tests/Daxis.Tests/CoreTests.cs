using System.Net;
using System.Text.Json.Nodes;
using Daxis.Core;
using Xunit;

namespace Daxis.Tests;

public sealed class DaxCompletionTests
{
    static readonly DaxSymbols Model = new(
        ["Sales", "Sales Data"],
        [("Sales", "Amount"), ("Sales", "Refund"), ("Sales Data", "Region")],
        ["Total Sales", "Headcount"]);

    [Fact]
    public void Bracket_ListsMeasuresAndColumns_FilteredByPrefix()
    {
        var (from, items) = DaxCompletion.Get("SUM([Tot", 8, Model);
        Assert.Equal(4, from);
        Assert.Equal("[Total Sales]", Assert.Single(items).Insert);
    }

    [Fact]
    public void QualifiedBracket_ListsOnlyThatTablesColumns()
    {
        var (_, items) = DaxCompletion.Get("'Sales Data'[", 13, Model);
        Assert.Equal("[Region]", Assert.Single(items).Insert);
    }

    [Fact]
    public void ClosedBracket_FallsBackToWordCompletion()
    {
        var (_, items) = DaxCompletion.Get("[Headcount] + SU", 16, Model);
        Assert.Contains(items, c => c.Insert == "SUM(");
        Assert.DoesNotContain(items, c => c.Kind == CompletionKind.Measure);
    }

    [Fact]
    public void Quote_ListsTablesQuotedWhenNeeded()
    {
        var (from, items) = DaxCompletion.Get("COUNTROWS('Sa", 13, Model);
        Assert.Equal(10, from);
        Assert.Equal(["Sales", "'Sales Data'"], items.Select(i => i.Insert));
    }

    [Fact]
    public void Word_IncludesVariablesDeclaredEarlier()
    {
        const string dax = "VAR total = 1\nRETURN tot";
        var (_, items) = DaxCompletion.Get(dax, dax.Length, Model);
        Assert.Equal("total", items[0].Insert);
    }

    [Fact]
    public void CaretAtZero_ReturnsNothing() => Assert.Empty(DaxCompletion.Get("SUM", 0, Model).Items);

    [Theory]
    [InlineData("'[", 2)]
    [InlineData("[", 1)]
    [InlineData("'", 1)]
    [InlineData("''[", 3)]
    public void EdgeInputs_NeverThrow(string text, int caret) => DaxCompletion.Get(text, caret, Model);
}

public sealed class FabricClientTests
{
    [Fact]
    public void ParseRows_UnionsColumnsAndKeepsNulls()
    {
        var rows = JsonNode.Parse("""[{"T[a]":1,"T[b]":"x"},{"T[a]":null,"T[c]":true}]""")!.AsArray();
        var r = FabricClient.ParseRows(rows);
        Assert.Equal(["T[a]", "T[b]", "T[c]"], r.Columns);
        Assert.Equal(new[] { "1", "x", null }, r.Rows[0]);
        Assert.Equal(new[] { null, null, "true" }, r.Rows[1]);
    }

    [Theory]
    [InlineData("https://api.fabric.microsoft.com/v1/workspaces", Auth.PowerBi, true)]
    [InlineData("https://api.powerbi.com/v1.0/myorg/groups", Auth.PowerBi, true)]
    [InlineData("https://wabi-australia-east-a-primary-redirect.analysis.windows.net/x", Auth.PowerBi, true)]
    [InlineData("https://onelake.dfs.fabric.microsoft.com/ws", Auth.Storage, true)]
    [InlineData("https://onelake.dfs.fabric.microsoft.com/ws", Auth.PowerBi, false)]     // wrong audience
    [InlineData("https://api.fabric.microsoft.com/v1/workspaces", Auth.Storage, false)]   // wrong audience
    [InlineData("https://app.powerbi.com/groups/x", Auth.PowerBi, false)]                 // portal, not API
    [InlineData("http://api.fabric.microsoft.com/v1/workspaces", Auth.PowerBi, false)]
    [InlineData("https://api.fabric.microsoft.com.evil.example/v1", Auth.PowerBi, false)]
    [InlineData("https://evil.example/?h=api.powerbi.com", Auth.PowerBi, false)]
    [InlineData("not a url", Auth.PowerBi, false)]
    public void IsTrusted_TokensOnlyReachTheirOwnMicrosoftApi(string url, string scope, bool expected) =>
        Assert.Equal(expected, FabricClient.IsTrusted(url, scope));

    [Theory]
    [InlineData("https://app.powerbi.com/groups/me/reports/1", true)]
    [InlineData("https://app.fabric.microsoft.com/groups/1/lakehouses/2", true)]
    [InlineData("https://evil.example/app.powerbi.com", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void IsPortalLink_OnlyHttpsFabricAndPowerBi(string? url, bool expected) =>
        Assert.Equal(expected, FabricClient.IsPortalLink(url));

    [Theory]
    [InlineData("""{"errorCode":"X","message":"Workspace not found"}""", "404: Workspace not found")]
    [InlineData("""{"error":{"code":"PowerBINotAuthorizedException"}}""", "404: PowerBINotAuthorizedException")]
    [InlineData("""{"error":"invalid_token"}""", "404 NotFound")]
    [InlineData("""[1,2]""", "404 NotFound")]
    [InlineData("not json", "404 NotFound")]
    public void ErrorMessage_ExtractsTheUsefulPart(string body, string expected) =>
        Assert.Equal(expected, FabricClient.ErrorMessage(HttpStatusCode.NotFound, body));
}
