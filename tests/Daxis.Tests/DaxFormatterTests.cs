using Daxis.Core;
using Xunit;

namespace Daxis.Tests;

public sealed class DaxFormatterTests
{
    [Fact]
    public void ShortLines_AlwaysExpandsFunctionArguments()
    {
        const string input = "sum(Sales[Amount])";

        var formatted = NormalizeNewLines(DaxFormatter.Format(input, DaxFormatMode.ShortLines));

        Assert.Equal(
            """
            SUM (
                Sales[Amount]
            )
            """,
            formatted);
    }

    [Fact]
    public void LongLines_KeepsNestedCallsInlineAndWrapsTopLevelArguments()
    {
        const string input = "calculate(sum(Sales[Amount]),filter(all(Sales),Sales[Year]>2020))";

        var formatted = NormalizeNewLines(DaxFormatter.Format(input, DaxFormatMode.LongLines));

        Assert.Equal(
            """
            CALCULATE (
                SUM ( Sales[Amount] ),
                FILTER ( ALL ( Sales ), Sales[Year] > 2020 )
            )
            """,
            formatted);
    }

    [Fact]
    public void Formatter_PreservesQualifiedColumnReferences()
    {
        const string input = "sumx('Sales Table','Sales Table'[Quantity]*'Sales Table'[Net Price])";

        var formatted = DaxFormatter.Format(input, DaxFormatMode.LongLines);

        Assert.Contains("'Sales Table'[Quantity]", formatted);
        Assert.Contains("'Sales Table'[Net Price]", formatted);
        Assert.DoesNotContain("'Sales Table' [", formatted);
    }

    private static string NormalizeNewLines(string value)
    {
        return value.Replace("\r\n", "\n");
    }
}
