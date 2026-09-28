using System.Text;

namespace Daxis.Core;

public enum DaxFormatMode
{
    /// <summary>Each function argument on its own line. Matches Tabular Editor short-lines style.</summary>
    ShortLines,
    /// <summary>Stays single-line up to 120 chars; only top-level args wrapped beyond that.</summary>
    LongLines,
    /// <summary>Collapse to a single compact line regardless of length.</summary>
    SingleLine
}

/// <summary>
/// Offline DAX formatter: uppercases keywords/functions, adds Tabular-Editor-style
/// function-call spacing ( FUNC ( args ) ), handles VAR/RETURN blocks.
/// No external API calls — runs fully in-process.
/// </summary>
public static class DaxFormatter
{
    // ── Token infrastructure ──

    public enum TokKind
    {
        Identifier, BracketIdent, SingleQuotedIdent, StringLiteral,
        Number, OpenParen, CloseParen, Comma, Operator,
        LineComment, BlockComment, Whitespace, NewLine,
    }

    public sealed record Token(TokKind Kind, string Text);

    // ── Keywords ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAR", "RETURN", "IN", "NOT", "AND", "OR", "TRUE", "FALSE",
        "DEFINE", "EVALUATE", "MEASURE", "ORDER", "BY", "ASC", "DESC",
        "START", "AT", "TOPN"
    };

    // ── Public entry point ────────────────────────────────────────────────────

    public static string Format(string input, DaxFormatMode mode = DaxFormatMode.ShortLines)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var tokens    = Tokenize(input);
        var normalised = tokens.Select(NormalizeCase).ToList();
        var sig        = normalised
            .Where(t => t.Kind is not TokKind.Whitespace and not TokKind.NewLine)
            .ToList();

        return mode switch
        {
            DaxFormatMode.SingleLine => BuildCompact(sig),
            DaxFormatMode.LongLines  => BuildLongLines(sig),
            _                        => BuildShortLines(sig)
        };
    }

    // ── Tokenizer (public for reuse) ──────────────────────────────────────────

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0, n = text.Length;

        while (i < n)
        {
            char c = text[i];

            // Line comment  //
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int start = i;
                while (i < n && text[i] != '\r' && text[i] != '\n') i++;
                tokens.Add(new Token(TokKind.LineComment, text[start..i]));
                continue;
            }

            // Block comment  /* */
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int start = i; i += 2;
                while (i < n - 1 && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i += 2;
                tokens.Add(new Token(TokKind.BlockComment, text[start..i]));
                continue;
            }

            // Newline
            if (c == '\r' || c == '\n')
            {
                int start = i;
                if (c == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
                i++;
                tokens.Add(new Token(TokKind.NewLine, text[start..i]));
                continue;
            }

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                int start = i;
                while (i < n && char.IsWhiteSpace(text[i]) && text[i] != '\r' && text[i] != '\n') i++;
                tokens.Add(new Token(TokKind.Whitespace, text[start..i]));
                continue;
            }

            // String literal: "..."
            if (c == '"')
            {
                int start = i++;
                while (i < n && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                if (i < n) i++;
                tokens.Add(new Token(TokKind.StringLiteral, text[start..i]));
                continue;
            }

            // Single-quoted identifier: 'TableName'
            if (c == '\'')
            {
                int start = i++;
                while (i < n && text[i] != '\'') i++;
                if (i < n) i++;
                tokens.Add(new Token(TokKind.SingleQuotedIdent, text[start..i]));
                continue;
            }

            // Bracket identifier: [Column]
            if (c == '[')
            {
                int start = i++;
                while (i < n && text[i] != ']') i++;
                if (i < n) i++;
                tokens.Add(new Token(TokKind.BracketIdent, text[start..i]));
                continue;
            }

            // Parentheses
            if (c == '(') { tokens.Add(new Token(TokKind.OpenParen,  "(")); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokKind.CloseParen, ")")); i++; continue; }

            // Comma
            if (c == ',') { tokens.Add(new Token(TokKind.Comma, ",")); i++; continue; }

            // Number (including negative literals after operator context)
            if (char.IsDigit(c) || (c == '-' && i + 1 < n && char.IsDigit(text[i + 1]) && !IsTrailingExpr(tokens)))
            {
                int start = i;
                if (c == '-') i++;
                while (i < n && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == 'E' || text[i] == 'e' ||
                                  ((text[i] == '+' || text[i] == '-') && i > 0 && (text[i - 1] == 'E' || text[i - 1] == 'e'))))
                    i++;
                tokens.Add(new Token(TokKind.Number, text[start..i]));
                continue;
            }

            // Compound operators
            if (c == '<' && i + 1 < n && text[i + 1] == '>') { tokens.Add(new Token(TokKind.Operator, "<>")); i += 2; continue; }
            if (c == '<' && i + 1 < n && text[i + 1] == '=') { tokens.Add(new Token(TokKind.Operator, "<=")); i += 2; continue; }
            if (c == '>' && i + 1 < n && text[i + 1] == '=') { tokens.Add(new Token(TokKind.Operator, ">=")); i += 2; continue; }
            if (c == '|' && i + 1 < n && text[i + 1] == '|') { tokens.Add(new Token(TokKind.Operator, "||")); i += 2; continue; }
            if (c == '&' && i + 1 < n && text[i + 1] == '&') { tokens.Add(new Token(TokKind.Operator, "&&")); i += 2; continue; }
            if ("=<>+-*/&@".Contains(c)) { tokens.Add(new Token(TokKind.Operator, c.ToString())); i++; continue; }

            // Identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++;
                tokens.Add(new Token(TokKind.Identifier, text[start..i]));
                continue;
            }

            tokens.Add(new Token(TokKind.Operator, c.ToString()));
            i++;
        }

        return tokens;
    }

    // ── Case normalisation ────────────────────────────────────────────────────

    private static Token NormalizeCase(Token t)
    {
        if (t.Kind != TokKind.Identifier) return t;
        if (Keywords.Contains(t.Text) || DaxFunctions.TryGet(t.Text) != null)
            return t with { Text = t.Text.ToUpperInvariant() };
        return t;
    }

    private static bool IsTrailingExpr(List<Token> tokens)
    {
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind is TokKind.Whitespace or TokKind.NewLine) continue;
            return t.Kind is TokKind.BracketIdent or TokKind.SingleQuotedIdent
                         or TokKind.Identifier or TokKind.Number or TokKind.CloseParen;
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A ( is a function-call open when the immediately preceding significant token
    /// is an Identifier (function name) or CloseParen (e.g. EARLIER(...)(...)  ).
    /// </summary>
    private static bool IsFunctionCallOpen(int idx, List<Token> sig)
    {
        if (idx == 0) return false;
        var prev = sig[idx - 1];
        return prev.Kind is TokKind.Identifier or TokKind.CloseParen;
    }

    private static bool ShouldJoinWithoutSpace(Token? prev, Token current)
    {
        return current.Kind == TokKind.BracketIdent &&
               prev?.Kind is TokKind.Identifier or TokKind.SingleQuotedIdent;
    }

    private static void AppendIndent(StringBuilder sb, int depth)
    {
        for (int i = 0; i < depth; i++) sb.Append("    ");
    }

    // Trim trailing spaces from the builder (not newlines)
    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    // ── Compact builder (single line, Tabular-Editor spacing) ─────────────────
    // Produces:  CALCULATE ( SUM ( x ), FILTER ( t, c ) )

    private static string BuildCompact(List<Token> sig)
    {
        var sb            = new StringBuilder();
        var funcCallStack = new Stack<bool>();

        for (int i = 0; i < sig.Count; i++)
        {
            var t    = sig[i];
            var next = i + 1 < sig.Count ? sig[i + 1] : null;

            switch (t.Kind)
            {
                case TokKind.OpenParen:
                {
                    bool isFc = IsFunctionCallOpen(i, sig);
                    funcCallStack.Push(isFc);
                    if (isFc) TrimTrailingSpaces(sb);
                    if (isFc && sb.Length > 0) sb.Append(' ');
                    sb.Append('(');
                    if (isFc && next?.Kind != TokKind.CloseParen) sb.Append(' ');
                    break;
                }
                case TokKind.CloseParen:
                {
                    bool wasFc = funcCallStack.Count > 0 && funcCallStack.Pop();
                    TrimTrailingSpaces(sb);
                    if (wasFc && sb.Length > 0 && sb[^1] != '(') sb.Append(' ');
                    sb.Append(')');
                    break;
                }
                case TokKind.Comma:
                    TrimTrailingSpaces(sb);
                    sb.Append(", ");
                    break;

                case TokKind.Operator:
                    TrimTrailingSpaces(sb);
                    if (sb.Length > 0 && sb[^1] != '(' && sb[^1] != ' ') sb.Append(' ');
                    sb.Append(t.Text);
                    if (next?.Kind is not TokKind.CloseParen and not TokKind.Comma) sb.Append(' ');
                    break;

                case TokKind.LineComment:
                case TokKind.BlockComment:
                    // Strip comments in single-line mode
                    break;

                default:
                    // Add space separator between adjacent value tokens
                    if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '(')
                    {
                        var prev = i > 0 ? sig[i - 1] : null;
                        if (!ShouldJoinWithoutSpace(prev, t) &&
                            prev?.Kind is TokKind.BracketIdent or TokKind.SingleQuotedIdent
                                          or TokKind.Identifier or TokKind.Number
                                          or TokKind.CloseParen or TokKind.StringLiteral)
                            sb.Append(' ');
                    }
                    sb.Append(t.Text);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    // ── Short-lines builder ───────────────────────────────────────────────────
    // Every function argument on its own indented line.
    // VAR and RETURN are placed on their own lines.
    // Matches Tabular Editor "short lines" style.

    private static string BuildShortLines(List<Token> sig)
    {
        var sb            = new StringBuilder();
        int depth         = 0;
        var funcCallStack = new Stack<bool>();

        for (int i = 0; i < sig.Count; i++)
        {
            var t    = sig[i];
            var next = i + 1 < sig.Count ? sig[i + 1] : null;

            // ── VAR / RETURN at top level ────────────────────────────────────
            if (t.Kind == TokKind.Identifier && depth == 0)
            {
                if (t.Text.Equals("VAR", StringComparison.OrdinalIgnoreCase))
                {
                    // Blank line before each VAR (except at very start)
                    if (sb.Length > 0) { TrimTrailingSpaces(sb); sb.AppendLine(); sb.AppendLine(); }
                    sb.Append("VAR ");
                    continue;
                }
                if (t.Text.Equals("RETURN", StringComparison.OrdinalIgnoreCase))
                {
                    TrimTrailingSpaces(sb);
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine("RETURN");
                    AppendIndent(sb, 1);
                    continue;
                }
            }

            // ── Open paren ───────────────────────────────────────────────────
            if (t.Kind == TokKind.OpenParen)
            {
                bool isFc = IsFunctionCallOpen(i, sig);
                funcCallStack.Push(isFc);
                depth++;

                TrimTrailingSpaces(sb);
                if (isFc && sb.Length > 0) sb.Append(' ');
                sb.Append('(');

                if (next?.Kind != TokKind.CloseParen) // non-empty parens
                {
                    sb.AppendLine();
                    AppendIndent(sb, depth);
                }
                continue;
            }

            // ── Close paren ──────────────────────────────────────────────────
            if (t.Kind == TokKind.CloseParen)
            {
                funcCallStack.TryPop(out _);
                depth--;

                TrimTrailingSpaces(sb);
                sb.AppendLine();
                AppendIndent(sb, depth);
                sb.Append(')');
                continue;
            }

            // ── Comma ────────────────────────────────────────────────────────
            if (t.Kind == TokKind.Comma)
            {
                TrimTrailingSpaces(sb);
                sb.Append(',');
                sb.AppendLine();
                AppendIndent(sb, depth);
                continue;
            }

            // ── Operator ─────────────────────────────────────────────────────
            if (t.Kind == TokKind.Operator)
            {
                TrimTrailingSpaces(sb);
                if (sb.Length > 0 && sb[^1] != '(' && sb[^1] != '\n') sb.Append(' ');
                sb.Append(t.Text);
                if (next?.Kind is not TokKind.CloseParen and not TokKind.Comma) sb.Append(' ');
                continue;
            }

            // ── Comments ─────────────────────────────────────────────────────
            if (t.Kind is TokKind.LineComment or TokKind.BlockComment)
            {
                sb.Append(t.Text);
                sb.AppendLine();
                AppendIndent(sb, depth);
                continue;
            }

            // ── Everything else (identifiers, literals, numbers) ─────────────
            // Add space separator if the buffer doesn't already end with one
            var prevToken = i > 0 ? sig[i - 1] : null;
            if (!ShouldJoinWithoutSpace(prevToken, t) &&
                sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n' && sb[^1] != '(')
                sb.Append(' ');

            sb.Append(t.Text);
        }

        return sb.ToString().TrimEnd();
    }

    // ── Long-lines builder ────────────────────────────────────────────────────
    // Only the direct arguments of the OUTERMOST function call get their own line.
    // Nested calls stay inline. Matches Tabular Editor "long lines" style.

    private static string BuildLongLines(List<Token> sig)
    {
        var sb            = new StringBuilder();
        int depth         = 0;
        var funcCallStack = new Stack<bool>();

        for (int i = 0; i < sig.Count; i++)
        {
            var t    = sig[i];
            var next = i + 1 < sig.Count ? sig[i + 1] : null;

            // VAR/RETURN at top level
            if (t.Kind == TokKind.Identifier && depth == 0)
            {
                if (t.Text.Equals("VAR", StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0) { TrimTrailingSpaces(sb); sb.AppendLine(); sb.AppendLine(); }
                    sb.Append("VAR ");
                    continue;
                }
                if (t.Text.Equals("RETURN", StringComparison.OrdinalIgnoreCase))
                {
                    TrimTrailingSpaces(sb);
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine("RETURN");
                    AppendIndent(sb, 1);
                    continue;
                }
            }

            if (t.Kind == TokKind.OpenParen)
            {
                bool isFc = IsFunctionCallOpen(i, sig);
                funcCallStack.Push(isFc);
                depth++;

                TrimTrailingSpaces(sb);
                if (isFc && sb.Length > 0) sb.Append(' ');
                sb.Append('(');

                // Only break after outermost ( (depth is now 1)
                if (depth == 1 && next?.Kind != TokKind.CloseParen)
                {
                    sb.AppendLine();
                    AppendIndent(sb, depth);
                }
                else if (isFc && next?.Kind != TokKind.CloseParen)
                {
                    sb.Append(' '); // inline space after function-call (
                }
                continue;
            }

            if (t.Kind == TokKind.CloseParen)
            {
                funcCallStack.TryPop(out bool wasFc);
                depth--;

                TrimTrailingSpaces(sb);
                if (depth == 0)
                {
                    // Closing outermost — own line
                    sb.AppendLine();
                    sb.Append(')');
                }
                else
                {
                    // Inline close for nested function calls
                    if (wasFc && sb.Length > 0 && sb[^1] != '(') sb.Append(' ');
                    sb.Append(')');
                }
                continue;
            }

            if (t.Kind == TokKind.Comma)
            {
                TrimTrailingSpaces(sb);
                sb.Append(',');
                if (depth == 1) { sb.AppendLine(); AppendIndent(sb, depth); }
                else sb.Append(' ');
                continue;
            }

            if (t.Kind == TokKind.Operator)
            {
                TrimTrailingSpaces(sb);
                if (sb.Length > 0 && sb[^1] != '(' && sb[^1] != '\n') sb.Append(' ');
                sb.Append(t.Text);
                if (next?.Kind is not TokKind.CloseParen and not TokKind.Comma) sb.Append(' ');
                continue;
            }

            if (t.Kind is TokKind.LineComment or TokKind.BlockComment)
            {
                sb.Append(t.Text);
                sb.AppendLine();
                AppendIndent(sb, depth);
                continue;
            }

            // Space separator
            var prevToken = i > 0 ? sig[i - 1] : null;
            if (!ShouldJoinWithoutSpace(prevToken, t) &&
                sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n' && sb[^1] != '(')
                sb.Append(' ');

            sb.Append(t.Text);
        }

        return sb.ToString().TrimEnd();
    }
}
