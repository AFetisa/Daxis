using System.Text;
using System.Text.RegularExpressions;

namespace Daxis.Core;

public sealed record MStep(string Name, string RawName, string Expression, string Lead);
public sealed record MSource(string Connector, string Target);

/// <summary>
/// Lightweight Power Query (M) reader: applied steps, data sources, query references, formatting.
/// Scans past strings, #"quoted identifiers" and comments; no full parser needed for these jobs.
/// </summary>
public static partial class MQuery
{
    /// <summary>Top-level <c>let</c> steps, or empty when the query is not a let expression.</summary>
    public static IReadOnlyList<MStep> Steps(string? m) => Parse(m)?.Steps ?? [];

    /// <summary>The literal a parameter query holds, e.g. <c>"sql-prod" meta [...]</c> → <c>"sql-prod"</c>.</summary>
    public static string ParameterValue(string? m) =>
        m is null ? "" : Regex.Match(m, @"^\s*(""(?:[^""]|"""")*""|[^\s]+)").Groups[1].Value;

    /// <summary>Replaces parameter names in connector arguments with their values so sources read as real endpoints.</summary>
    public static string SubstituteParameters(string target, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var (name, value) in parameters)
            if (value.Length > 0 && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                target = Regex.Replace(target, $@"(?<![\w""#]){Regex.Escape(name)}(?![\w""])", value.Replace("$", "$$"));
        return target;
    }

    public static bool IsParameter(string? m) => m is not null && ParameterRegex().IsMatch(m);

    [GeneratedRegex(@"IsParameterQuery\s*=\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterRegex();

    // Connector functions that read data. Order is irrelevant; matches are de-duplicated.
    [GeneratedRegex(@"\b(Sql\.Databases?|Lakehouse\.Contents|Fabric\.[A-Za-z]+|PowerPlatform\.Dataflows|PowerBI\.Dataflows|Web\.Contents|Excel\.Workbook|Csv\.Document|Json\.Document|Xml\.Tables|SharePoint\.(?:Files|Contents|Tables)|AzureStorage\.(?:DataLake|Blobs|Tables)|Snowflake\.Databases|Databricks\.(?:Catalogs|Query)|DatabricksMultiCloud\.Catalogs|OData\.Feed|Odbc\.(?:DataSource|Query)|OleDb\.DataSource|Oracle\.Database|PostgreSQL\.Database|MySQL\.Database|AnalysisServices\.Database|GoogleBigQuery\.Database|AmazonRedshift\.Database|Folder\.Files|File\.Contents|Salesforce\.(?:Data|Reports)|Dataverse\.Contents|CommonDataService\.Database|Kusto\.Contents|AzureDataExplorer\.Contents)\s*\((?=([^()]*))")]
    private static partial Regex SourceRegex();

    public static IReadOnlyList<MSource> Sources(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return [];
        var list = SourceRegex().Matches(m)
            .Select(x => new MSource(x.Groups[1].Value, Shorten(x.Groups[2].Value)))
            .ToList();
        if (m.Contains("Binary.FromText(")) list.Add(new MSource("Entered data", ""));
        else if (m.Contains("#table(")) list.Add(new MSource("Inline table", ""));
        // Excel.Workbook(File.Contents("x")) matches both; keep the inner call that carries the real target.
        list.RemoveAll(s => list.Any(o => o != s && s.Target.StartsWith(o.Connector)));
        if (list.Count > 1) list.RemoveAll(s => s.Target.Length == 0 && list.Any(o => o.Target.Length > 0));
        return list.Distinct().ToList();
    }

    static string Shorten(string args)
    {
        var s = Regex.Replace(args, @"\s+", " ").Trim().TrimEnd(',');
        return s.Length > 90 ? s[..87] + "…" : s;
    }

    /// <summary>Which of <paramref name="queryNames"/> this expression references (staging queries, parameters).</summary>
    public static IReadOnlyList<string> References(string? m, IEnumerable<string> queryNames)
    {
        if (string.IsNullOrWhiteSpace(m)) return [];
        var code = StripStrings(m);
        return queryNames.Where(n =>
                m.Contains($"#\"{n.Replace("\"", "\"\"")}\"") ||
                (Regex.IsMatch(n, @"^[A-Za-z_][A-Za-z0-9_]*$") && Regex.IsMatch(code, $@"(?<![\w.#""]){Regex.Escape(n)}(?![\w""])")))
            .ToList();
    }

    /// <summary>Normalises layout: one step per line, 4-space indent, body lines re-indented. Never touches string contents.</summary>
    public static string Format(string m)
    {
        var p = Parse(m);
        if (p is null || p.Steps.Count == 0) return m.Trim();
        // Keep anything before the top-level let verbatim: header comments, a function signature "(x as text) =>".
        var sb = new StringBuilder(p.Prefix.TrimEnd()).Append(p.Prefix.Trim().Length > 0 ? "\n" : "").Append("let\n");
        for (var i = 0; i < p.Steps.Count; i++)
        {
            var s = p.Steps[i];
            foreach (var line in s.Lead.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                sb.Append("    ").Append(line.Trim()).Append('\n');
            var expr = Reindent(s.Expression, 8);
            sb.Append("    ").Append(s.RawName).Append(" = ").Append(expr);
            // A trailing // comment would swallow the separator, so put it on its own line.
            var sep = i < p.Steps.Count - 1 ? "," : "";
            sb.Append(EndsWithLineComment(expr) ? $"\n    {sep}\n" : $"{sep}\n");
        }
        sb.Append("in\n    ").Append(Reindent(p.Result, 4));
        return sb.ToString();
    }

    static string Reindent(string expr, int indent)
    {
        var text = expr.Trim();
        if (HasMultilineString(text)) return text; // re-indenting would change the literal
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 1) return text;
        var rest = lines.Skip(1).Where(l => l.Trim().Length > 0).ToList();
        var min = rest.Count == 0 ? 0 : rest.Min(l => l.Length - l.TrimStart().Length);
        var pad = new string(' ', indent);
        return lines[0].Trim() + "\n" + string.Join("\n",
            lines.Skip(1).Select(l => l.Trim().Length == 0 ? "" : pad + l[Math.Min(min, l.Length - l.TrimStart().Length)..].TrimEnd()));
    }

    // ── Scanner ──────────────────────────────────────────────────────────────

    sealed record Let(string Prefix, List<MStep> Steps, string Result);

    static bool EndsWithLineComment(string expr)
    {
        var last = expr[(expr.LastIndexOf('\n') + 1)..];
        for (var i = 0; i < last.Length - 1; i++)
        {
            if (last[i] == '"') { i = SkipString(last, i) - 1; continue; }
            if (last[i] == '/' && last[i + 1] == '/') return true;
        }
        return false;
    }

    static Let? Parse(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return null;
        var stack = new Stack<char>(); // ( [ { and L for let
        int? bodyStart = null;
        var letAt = 0;
        var segStart = -1;
        var parts = new List<string>();
        var inEnd = -1;

        for (var i = 0; i < m.Length;)
        {
            var c = m[i];
            var next = i + 1 < m.Length ? m[i + 1] : '\0';
            if (c == '"') { i = SkipString(m, i); continue; }
            if (c == '#' && next == '"') { i = SkipString(m, i + 1); continue; }
            if (c == '/' && next == '/') { i = m.IndexOf('\n', i) is var n and >= 0 ? n : m.Length; continue; }
            if (c == '/' && next == '*') { i = m.IndexOf("*/", i + 2, StringComparison.Ordinal) is var n and >= 0 ? n + 2 : m.Length; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var s = i;
                while (i < m.Length && (char.IsLetterOrDigit(m[i]) || m[i] is '_' or '.')) i++;
                var word = m[s..i];
                if (word == "let")
                {
                    if (stack.Count == 0 && bodyStart is null) { segStart = (bodyStart = i).Value; letAt = s; }
                    stack.Push('L');
                }
                else if (word == "in" && stack.Count > 0 && stack.Peek() == 'L')
                {
                    stack.Pop();
                    if (stack.Count == 0 && bodyStart is not null && inEnd < 0)
                    {
                        parts.Add(m[segStart..s]);
                        inEnd = i;
                    }
                }
                continue;
            }
            if (c is '(' or '[' or '{') stack.Push(c);
            else if (c is ')' or ']' or '}') { if (stack.Count > 0) stack.Pop(); }
            else if (c == ',' && stack.Count == 1 && stack.Peek() == 'L' && bodyStart is not null && inEnd < 0)
            {
                parts.Add(m[segStart..i]);
                segStart = i + 1;
            }
            i++;
        }
        if (bodyStart is null || inEnd < 0) return null;

        var steps = new List<MStep>();
        foreach (var part in parts)
        {
            var (lead, rest) = SplitLeadingComments(part);
            var eq = IndexOfOutsideQuotes(rest, '=');
            if (eq < 0) continue;
            var raw = rest[..eq].Trim();
            var name = raw.StartsWith("#\"") && raw.EndsWith('"') ? raw[2..^1].Replace("\"\"", "\"") : raw;
            steps.Add(new MStep(name, raw, rest[(eq + 1)..], lead));
        }
        return new Let(m[..letAt], steps, m[inEnd..].Trim());
    }

    static (string Lead, string Body) SplitLeadingComments(string part)
    {
        var lead = new StringBuilder();
        var rest = part.TrimStart();
        while (true)
        {
            if (rest.StartsWith("//"))
            {
                var nl = rest.IndexOf('\n');
                lead.Append(nl < 0 ? rest : rest[..nl]).Append('\n');
                rest = nl < 0 ? "" : rest[(nl + 1)..].TrimStart();
            }
            else if (rest.StartsWith("/*"))
            {
                var end = rest.IndexOf("*/", StringComparison.Ordinal);
                var cut = end < 0 ? rest.Length : end + 2;
                lead.Append(rest[..cut]).Append('\n');
                rest = rest[cut..].TrimStart();
            }
            else return (lead.ToString(), rest);
        }
    }

    static int IndexOfOutsideQuotes(string s, char target)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') { i = SkipString(s, i) - 1; continue; }
            if (s[i] == target) return i;
        }
        return -1;
    }

    // i points at the opening quote; returns the index after the closing quote ("" is an escaped quote).
    static int SkipString(string s, int i)
    {
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] != '"') continue;
            if (j + 1 < s.Length && s[j + 1] == '"') { j++; continue; }
            return j + 1;
        }
        return s.Length;
    }

    static bool HasMultilineString(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '"') continue;
            var end = SkipString(s, i);
            if (s.AsSpan(i, end - i).Contains('\n')) return true;
            i = end - 1;
        }
        return false;
    }

    static string StripStrings(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"' && (i == 0 || s[i - 1] != '#')) { var end = SkipString(s, i); sb.Append('"', 2); i = end - 1; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
