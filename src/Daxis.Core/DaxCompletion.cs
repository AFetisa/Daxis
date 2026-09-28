using System.Text.RegularExpressions;

namespace Daxis.Core;

public sealed record DaxSymbols(
    IReadOnlyList<string> Tables,
    IReadOnlyList<(string Table, string Column)> Columns,
    IReadOnlyList<string> Measures)
{
    public static readonly DaxSymbols Empty = new([], [], []);
}

public enum CompletionKind { Function, Table, Column, Measure, Variable }

public sealed record Completion(string Label, string Insert, CompletionKind Kind, string Detail);

/// <summary>
/// Context-aware DAX suggestions. <c>ReplaceFrom</c> is the offset the insert text replaces up to the caret.
/// </summary>
public static partial class DaxCompletion
{
    [GeneratedRegex(@"\bVAR\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex VarRegex();

    public static (int ReplaceFrom, List<Completion> Items) Get(string text, int caret, DaxSymbols symbols)
    {
        if (caret <= 0 || caret > text.Length) return (caret, []);

        // Inside [ ... → measures and columns
        var bracket = OpenBracket(text, caret);
        if (bracket >= 0)
        {
            var prefix = text[(bracket + 1)..caret];
            var table = TableBefore(text, bracket);
            var cols = symbols.Columns.Where(c => table is null || c.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
            var items = (table is null ? symbols.Measures.Select(m => new Completion(m, $"[{m}]", CompletionKind.Measure, "Measure")) : [])
                .Concat(cols.Select(c => new Completion(c.Column, $"[{c.Column}]", CompletionKind.Column, $"{c.Table}[{c.Column}]")))
                .Where(c => c.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(c => c.Insert + c.Detail)
                .OrderBy(c => !c.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ThenBy(c => c.Label)
                .ToList();
            return (bracket, items);
        }

        // Inside ' ... → tables
        var quote = OpenQuote(text, caret);
        if (quote >= 0)
        {
            var prefix = text[(quote + 1)..caret];
            return (quote, symbols.Tables
                .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(t => new Completion(t, Quote(t), CompletionKind.Table, "Table")).ToList());
        }

        // Bare word → functions, tables, variables
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '.')) start--;
        var word = text[start..caret];
        if (word.Length == 0 || char.IsDigit(word[0])) return (caret, []);

        var vars = VarRegex().Matches(text[..start]).Select(m => m.Groups[1].Value).Distinct()
            .Select(v => new Completion(v, v, CompletionKind.Variable, "Variable"));
        var fns = DaxFunctions.All.Select(f => new Completion(f.Name, f.Name + "(", CompletionKind.Function,
            $"{f.Name}({string.Join(", ", f.Parameters)})\n{f.Description}"));
        var tables = symbols.Tables.Select(t => new Completion(t, Quote(t), CompletionKind.Table, "Table"));

        return (start, vars.Concat(fns).Concat(tables)
            .Where(c => c.Label.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    /// <summary>Quotes a table name only when DAX requires it.</summary>
    public static string Quote(string table) =>
        Regex.IsMatch(table, @"^[A-Za-z_][A-Za-z0-9_]*$") ? table : $"'{table.Replace("'", "''")}'";

    // Unclosed `[` on the caret's line, or -1.
    static int OpenBracket(string text, int caret)
    {
        for (var i = caret - 1; i >= 0 && text[i] != '\n'; i--)
        {
            if (text[i] == ']') return -1;
            if (text[i] == '[') return i;
        }
        return -1;
    }

    // Unclosed `'` on the caret's line (odd quote count), or -1.
    static int OpenQuote(string text, int caret)
    {
        int count = 0, last = -1;
        for (var i = text.LastIndexOf('\n', caret - 1) + 1; i < caret; i++)
            if (text[i] == '\'') { count++; last = i; }
        return count % 2 == 1 ? last : -1;
    }

    // `Sales[` or `'Sales Data'[` immediately before the bracket.
    static string? TableBefore(string text, int bracket)
    {
        if (bracket == 0) return null;
        if (text[bracket - 1] == '\'')
        {
            var open = bracket >= 2 ? text.LastIndexOf('\'', bracket - 2) : -1;
            return open >= 0 ? text[(open + 1)..(bracket - 1)].Replace("''", "'") : null;
        }
        var s = bracket;
        while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
        return s < bracket ? text[s..bracket] : null;
    }
}
