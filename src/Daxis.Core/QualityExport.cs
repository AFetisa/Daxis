using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daxis.Core;

public sealed record ScoredModel(string Workspace, string Model, QualityReport Report);

/// <summary>Quality results as files: an HTML or Markdown report for people, JSON and CSV for loading into a model.</summary>
public static class QualityExport
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Json(IReadOnlyList<ScoredModel> models) => JsonSerializer.Serialize(new
    {
        Generated = DateTimeOffset.Now,
        Estate = ModelQuality.Rollup(models.Select(m => m.Report).ToList()),
        Models = models,
    }, JsonOptions);

    public static string ScoresCsv(IReadOnlyList<ScoredModel> models)
    {
        var areas = Enum.GetValues<QualityArea>();
        var sb = new StringBuilder();
        Row(sb, ["Workspace", "Model", "Score", "Grade", .. areas.Select(a => a.ToString()), "Complexity", "Complexity band",
            "Errors", "Warnings", "Info", "Suppressed", "Cap"]);
        foreach (var m in models)
            Row(sb, [m.Workspace, m.Model, $"{m.Report.Score:0.0}", m.Report.Grade,
                .. areas.Select(a => m.Report.Areas.FirstOrDefault(s => s.Area == a) is { } s ? $"{s.Score:0.0}" : ""),
                $"{m.Report.Complexity.Index}", m.Report.Complexity.Band, $"{m.Report.Errors}", $"{m.Report.Warnings}", $"{m.Report.Infos}",
                $"{m.Report.Suppressed}", m.Report.CapReason ?? ""]);
        return sb.ToString();
    }

    public static string FindingsCsv(IReadOnlyList<ScoredModel> models)
    {
        var sb = new StringBuilder();
        Row(sb, ["Workspace", "Model", "Rule ID", "Area", "Severity", "Rule", "Table", "Object", "Detail", "Suppressed", "Advice"]);
        foreach (var m in models)
            foreach (var f in m.Report.Findings)
                Row(sb, [m.Workspace, m.Model, f.RuleId, f.Area.ToString(), f.Severity.ToString(), f.Rule, f.Table, f.Object, f.Detail,
                    f.Suppressed ? "Yes" : "No", f.Advice]);
        return sb.ToString();
    }

    static void Row(StringBuilder sb, IEnumerable<string> cells) =>
        sb.AppendLine(string.Join(",", cells.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{c.Replace("\"", "\"\"")}\"" : c)));

    public static string Markdown(IReadOnlyList<ScoredModel> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Semantic model quality").AppendLine();
        sb.AppendLine($"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm}. Scores run 0–100 (A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 60, F below).").AppendLine();
        if (models.Count > 1)
        {
            var r = ModelQuality.Rollup(models.Select(m => m.Report).ToList());
            sb.AppendLine($"**Estate: {r.Grade} ({r.Score:0})** across {r.Models} models · " +
                string.Join(" · ", r.Grades.Select(g => $"{g.Key} {g.Value}")) + $" · {r.Errors} errors, {r.Warnings} warnings").AppendLine();
            sb.AppendLine("| Workspace | Model | Grade | Score | Complexity | Errors | Warnings |").AppendLine("|---|---|---|---|---|---|---|");
            foreach (var m in models.OrderBy(m => m.Report.Score))
                sb.AppendLine($"| {Md(m.Workspace)} | {Md(m.Model)} | {m.Report.Grade} | {m.Report.Score:0} | {m.Report.Complexity.Band} | {m.Report.Errors} | {m.Report.Warnings} |");
            sb.AppendLine();
        }
        foreach (var m in models)
        {
            var q = m.Report;
            sb.AppendLine($"## {Md(m.Model)} — {q.Grade} ({q.Score:0})").AppendLine();
            sb.AppendLine($"Workspace {Md(m.Workspace)} · complexity {q.Complexity.Band} ({q.Complexity.Index}/30)" +
                (q.CapReason is { } cap ? $" · {cap}" : "")).AppendLine();
            sb.AppendLine("| Area | Grade | Score | Findings |").AppendLine("|---|---|---|---|");
            foreach (var a in q.Areas) sb.AppendLine($"| {a.Area} | {a.Grade} | {a.Score:0} | {a.Findings} |");
            sb.AppendLine();
            if (q.Complexity.Drivers.Any())
                sb.AppendLine("Complexity drivers: " + string.Join(", ", q.Complexity.Drivers.Select(d => $"{d.Name} ({d.Value})"))).AppendLine();
            foreach (var g in Groups(q))
            {
                sb.AppendLine($"### {g.First().Severity} · {g.Key.RuleId} {Md(g.Key.Rule)} ({g.Count()})").AppendLine();
                sb.AppendLine(Md(g.First().Advice)).AppendLine();
                foreach (var f in g.Take(25)) sb.AppendLine($"- {Md(Path(f))}: {Md(f.Detail)}");
                if (g.Count() > 25) sb.AppendLine($"- …and {g.Count() - 25} more");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string Html(IReadOnlyList<ScoredModel> models)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Semantic model quality</title><style>
            body{font:14px/1.5 system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#1d2420;background:#fbfaf7}
            h1{font-size:1.6rem}h2{margin-top:2.5rem;border-top:1px solid #ddd;padding-top:1.5rem}
            table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}th,td{text-align:left;padding:.3rem .6rem;border-bottom:1px solid #e4e2dc;vertical-align:top}
            th{font-weight:600;color:#555}.g{display:inline-block;min-width:1.6em;text-align:center;font-weight:700;border-radius:4px;padding:0 .3em;color:#fff}
            .A{background:#1f7a4d}.B{background:#5f9a3a}.C{background:#c08a1e}.D{background:#c4612a}.F{background:#b3261e}
            .muted{color:#6b6f6a}details{margin:.4rem 0}summary{cursor:pointer}.Error{color:#b3261e}.Warning{color:#a8650f}.Info{color:#5b6470}
            </style></head><body><h1>Semantic model quality</h1>
            """);
        sb.Append($"<p class=muted>Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm}. Scores run 0–100: A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 60, F below. " +
                  "Any error caps a model at C; three error rules, a broken measure or a mismatched key cap it at D.</p>");
        if (models.Count > 1)
        {
            var r = ModelQuality.Rollup(models.Select(m => m.Report).ToList());
            sb.Append($"<p><span class=\"g {r.Grade}\">{r.Grade}</span> <b>{r.Score:0}</b> mean across {r.Models} models · " +
                      string.Join(" · ", r.Grades.Select(g => $"{g.Key} {g.Value}")) + $" · {r.Errors} errors · {r.Warnings} warnings</p>");
            sb.Append("<table><tr><th>Workspace<th>Model<th>Grade<th>Score<th>Complexity<th>Errors<th>Warnings</tr>");
            foreach (var m in models.OrderBy(m => m.Report.Score))
                sb.Append($"<tr><td>{H(m.Workspace)}<td><a href=\"#{Anchor(m)}\">{H(m.Model)}</a><td><span class=\"g {m.Report.Grade}\">{m.Report.Grade}</span>" +
                          $"<td>{m.Report.Score:0}<td>{m.Report.Complexity.Band}<td>{m.Report.Errors}<td>{m.Report.Warnings}</tr>");
            sb.Append("</table>");
        }
        foreach (var m in models)
        {
            var q = m.Report;
            sb.Append($"<h2 id=\"{Anchor(m)}\"><span class=\"g {q.Grade}\">{q.Grade}</span> {H(m.Model)} <span class=muted>{q.Score:0}</span></h2>");
            sb.Append($"<p class=muted>{H(m.Workspace)} · complexity {q.Complexity.Band} ({q.Complexity.Index}/30)" +
                      (q.CapReason is { } cap ? $" · {H(cap)}" : "") + "</p>");
            sb.Append("<table><tr><th>Area<th>Grade<th>Score<th>Findings</tr>");
            foreach (var a in q.Areas) sb.Append($"<tr><td>{a.Area}<td><span class=\"g {a.Grade}\">{a.Grade}</span><td>{a.Score:0}<td>{a.Findings}</tr>");
            sb.Append("</table>");
            if (q.Complexity.Drivers.Any())
                sb.Append("<p>Complexity drivers: " + H(string.Join(", ", q.Complexity.Drivers.Select(d => $"{d.Name} ({d.Value})"))) + "</p>");
            foreach (var g in Groups(q))
            {
                var first = g.First();
                sb.Append($"<details><summary><span class={first.Severity}>{first.Severity}</span> · {g.Key.RuleId} {H(g.Key.Rule)} ({g.Count()})</summary>");
                sb.Append($"<p>{H(first.Advice)}</p><table><tr><th>Object<th>Detail</tr>");
                foreach (var f in g) sb.Append($"<tr><td>{H(Path(f))}<td>{H(f.Detail)}{(f.Suppressed ? " <i class=muted>(suppressed)</i>" : "")}</tr>");
                sb.Append("</table></details>");
            }
        }
        return sb.Append("</body></html>").ToString();
    }

    static IEnumerable<IGrouping<(string RuleId, string Rule), Finding>> Groups(QualityReport q) =>
        q.Findings.GroupBy(f => (f.RuleId, f.Rule)).OrderByDescending(g => g.First().Severity).ThenByDescending(g => g.Count());

    static string Path(Finding f) => f.Table.Length == 0 || f.Table == f.Object ? f.Object : $"{f.Table} / {f.Object}";

    static string Anchor(ScoredModel m) => "m" + (uint)$"{m.Workspace}/{m.Model}".GetHashCode();

    static string Md(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
