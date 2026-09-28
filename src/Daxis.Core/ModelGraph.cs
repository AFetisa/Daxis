using Microsoft.AnalysisServices.Tabular;

namespace Daxis.Core;

public enum GKind { Source, Query, Table, Model, Report }

public sealed record GRow(string Text, string Hint);

/// <summary>A card on the canvas. X/Y/W/H are world coordinates set by the layout.</summary>
public sealed class GNode(string id, GKind kind, string title, string kicker, string detail)
{
    public string Id { get; } = id;
    public GKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Kicker { get; } = kicker;
    public string Detail { get; } = detail;
    public List<GRow> Rows { get; } = [];
    public string? Link { get; init; }
    public int Layer { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 232;
    public double H { get; set; } = 62;
}

/// <summary>Data (or filter) flows From → To. Rows anchor the ends on a key column when set.</summary>
public sealed record GEdge(GNode From, GNode To, int FromRow = -1, int ToRow = -1,
    string? FromLabel = null, string? ToLabel = null, bool Dashed = false, bool Both = false);

/// <summary><see cref="Layered"/> graphs read left to right and highlight whole upstream/downstream paths.</summary>
public sealed record Graph(IReadOnlyList<GNode> Nodes, IReadOnlyList<GEdge> Edges, bool Layered);

public static class ModelGraph
{
    const double Gap = 16, LayerGap = 120, Margin = 48;

    // ── Pipeline lineage: sources → staging queries → tables → reports ──────

    public static Graph Lineage(Model model, IEnumerable<(string Name, string? Link, IReadOnlySet<FieldRef> Fields)>? reports = null)
    {
        var nodes = new Dictionary<string, GNode>();
        var edges = new HashSet<(GNode, GNode)>();
        var shared = model.Expressions.Where(e => e.Kind == ExpressionKind.M).ToList();
        var parameters = shared.Where(e => MQuery.IsParameter(e.Expression)).ToDictionary(e => e.Name, e => MQuery.ParameterValue(e.Expression));
        var queryNames = shared.Where(e => !parameters.ContainsKey(e.Name)).Select(e => e.Name).ToList();
        var directLake = model.Tables.SelectMany(t => t.Partitions).Select(p => (p.Source as EntityPartitionSource)?.ExpressionSource)
            .OfType<NamedExpression>().ToHashSet();

        GNode Source(MSource s)
        {
            var target = MQuery.SubstituteParameters(s.Target, parameters).Replace("\"", "");
            var id = $"src:{s.Connector}|{target}";
            // A target without a literal (null, a step variable) says nothing; show the connector instead.
            var literal = s.Target.Contains('"') || parameters.Keys.Any(s.Target.Contains);
            return nodes.TryGetValue(id, out var n) ? n
                : nodes[id] = new GNode(id, GKind.Source, literal ? target : s.Connector, s.Connector, literal ? "data source" : "data source · no fixed target");
        }

        GNode Query(NamedExpression e) => nodes.TryGetValue("q:" + e.Name, out var n) ? n
            : nodes["q:" + e.Name] = new GNode("q:" + e.Name, GKind.Query, e.Name,
                directLake.Contains(e) ? "Direct Lake source" : "Staging query", Plural(MQuery.Steps(e.Expression).Count, "step"));

        void Feed(GNode to, string? m)
        {
            foreach (var s in MQuery.Sources(m)) edges.Add((Source(s), to));
            foreach (var r in MQuery.References(m, queryNames)) edges.Add((Query(shared.First(e => e.Name == r)), to));
        }

        foreach (var e in shared.Where(e => !parameters.ContainsKey(e.Name))) Feed(Query(e), e.Expression);

        var tables = model.Tables.Where(t => !ModelInsight.IsAutoDateTable(t)).ToList();
        var tableNodes = new Dictionary<Table, GNode>();
        foreach (var t in tables)
        {
            var p = t.Partitions.FirstOrDefault();
            var mode = p?.Mode is { } md && md != ModeType.Default ? md : model.DefaultMode;
            var steps = t.Partitions.Sum(x => x.Source is MPartitionSource ms ? MQuery.Steps(ms.Expression).Count : 0);
            var kicker = p?.Source switch
            {
                CalculatedPartitionSource => "DAX table",
                EntityPartitionSource => "Direct Lake",
                _ => mode.ToString(),
            };
            var detail = string.Join(" · ", new[]
            {
                steps > 0 ? Plural(steps, "step") : null,
                Plural(t.Columns.Count(c => c.Type != ColumnType.RowNumber), "column"),
                t.Measures.Count > 0 ? Plural(t.Measures.Count, "measure") : null,
            }.OfType<string>());
            var n = tableNodes[t] = nodes["t:" + t.Name] = new GNode("t:" + t.Name, GKind.Table, t.Name, kicker, detail);
            foreach (var part in t.Partitions)
                switch (part.Source)
                {
                    case MPartitionSource ms: Feed(n, ms.Expression); break;
                    case EntityPartitionSource { ExpressionSource: { } src }: edges.Add((Query(src), n)); break;
                    case QueryPartitionSource qs: edges.Add((Source(new MSource(qs.DataSource?.Name ?? "Native query", "")), n)); break;
                }
        }
        foreach (var t in tables)
            if (t.Partitions.FirstOrDefault()?.Source is CalculatedPartitionSource cs)
                foreach (var dep in Dax.Tables(cs.Expression, model).Where(d => d != t && tableNodes.ContainsKey(d)))
                    edges.Add((tableNodes[dep], tableNodes[t]));

        foreach (var (name, link, fields) in reports ?? [])
        {
            var used = fields.Select(f => f.IsMeasure
                    ? tables.FirstOrDefault(t => t.Measures.Find(f.Name) is not null)
                    : tables.FirstOrDefault(t => t.Name == f.Table))
                .OfType<Table>().Distinct().ToList();
            var r = nodes["r:" + link + name] = new GNode("r:" + link + name, GKind.Report, name, "Report", Plural(fields.Count, "field")) { Link = link };
            foreach (var t in used) edges.Add((tableNodes[t], r));
        }

        var g = new Graph(nodes.Values.ToList(), edges.Select(e => new GEdge(e.Item1, e.Item2)).ToList(), true);
        LayerLayout(g.Nodes, g.Edges, lineage: true);
        return g;
    }

    // ── Workspace lineage: sources → semantic models → reports ──────────────

    /// <summary>
    /// <paramref name="models"/> are this workspace's models; reports may point at models elsewhere, which appear
    /// as their own cards. <paramref name="modelLink"/> builds the portal link for a (workspace, model) pair.
    /// </summary>
    public static Graph Workspace(IReadOnlyList<FabricItem> models, IReadOnlyDictionary<string, IReadOnlyList<MSource>> sources,
        IReadOnlyList<WorkspaceReport> reports, Func<string, string, string> modelLink)
    {
        var nodes = new Dictionary<string, GNode>();
        var edges = new HashSet<(GNode, GNode)>();
        GNode Model(string id, string wsId, string name, string kicker) => nodes.TryGetValue("m:" + id, out var n) ? n
            : nodes["m:" + id] = new GNode("m:" + id, GKind.Model, name, kicker, "") { Link = modelLink(wsId, id) };

        foreach (var m in models)
        {
            var src = sources.GetValueOrDefault(m.Id) ?? [];
            var used = reports.Count(r => r.ModelId == m.Id);
            var node = nodes["m:" + m.Id] = new GNode("m:" + m.Id, GKind.Model, m.DisplayName, "Semantic model",
                $"{Plural(src.Count, "source")} · {Plural(used, "report")}") { Link = modelLink(m.WorkspaceId, m.Id) };
            foreach (var s in src)
            {
                var id = $"src:{s.Connector}|{s.Target}";
                var sn = nodes.TryGetValue(id, out var x) ? x
                    : nodes[id] = new GNode(id, GKind.Source, s.Target.Length > 0 ? s.Target : s.Connector, s.Connector, "data source");
                edges.Add((sn, node));
            }
        }
        foreach (var r in reports)
        {
            var model = Model(r.ModelId, r.ModelWorkspaceId, "Model in another workspace", "Semantic model · external");
            var rn = nodes["r:" + r.Ref.Id] = new GNode("r:" + r.Ref.Id, GKind.Report, r.Ref.Name, "Report", model.Title) { Link = r.Ref.WebUrl };
            edges.Add((model, rn));
        }
        var g = new Graph(nodes.Values.ToList(), edges.Select(e => new GEdge(e.Item1, e.Item2)).ToList(), true);
        LayerLayout(g.Nodes, g.Edges, lineage: true);
        return g;
    }

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// Longest-path layers, barycentre ordering, then y aligned to neighbours without overlap.
    /// <paramref name="lineage"/> lines tables up in one column and puts reports last.
    /// </summary>
    internal static void LayerLayout(IReadOnlyList<GNode> nodes, IReadOnlyList<GEdge> edges, bool lineage)
    {
        var preds = nodes.ToDictionary(n => n, _ => new List<GNode>());
        var succs = nodes.ToDictionary(n => n, _ => new List<GNode>());
        foreach (var e in edges) { preds[e.To].Add(e.From); succs[e.From].Add(e.To); }

        // Longest path from the roots; bounded so a cycle can't spin forever.
        void Relax(Func<GEdge, bool> which)
        {
            for (var pass = 0; pass < nodes.Count; pass++)
            {
                var changed = false;
                foreach (var e in edges.Where(which))
                    if (e.To.Layer < e.From.Layer + 1) { e.To.Layer = e.From.Layer + 1; changed = true; }
                if (!changed) break;
            }
        }
        foreach (var n in nodes) n.Layer = n.Kind == GKind.Source ? 0 : 1;
        Relax(_ => true);
        if (lineage)
        {
            // Tables share one column (calculated tables that read tables step right); orphans join them; reports go last.
            var tables = nodes.Where(n => n.Kind == GKind.Table).ToList();
            var fed = tables.Where(n => preds[n].Any(p => p.Kind != GKind.Table)).Select(n => n.Layer).DefaultIfEmpty(1).Max();
            foreach (var t in tables.Where(t => preds[t].All(p => p.Kind != GKind.Table))) t.Layer = fed;
            Relax(e => e.To.Kind == GKind.Table);
            var last = nodes.Where(n => n.Kind != GKind.Report).Select(n => n.Layer).DefaultIfEmpty(0).Max();
            foreach (var r in nodes.Where(n => n.Kind == GKind.Report)) r.Layer = last + 1;
        }

        var layers = nodes.GroupBy(n => n.Layer).OrderBy(x => x.Key)
            .Select(x => x.OrderBy(n => n.Kind).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ToList()).ToList();

        // Barycentre sweeps reduce crossings.
        var index = new Dictionary<GNode, double>();
        void Reindex() { foreach (var l in layers) for (var i = 0; i < l.Count; i++) index[l[i]] = i; }
        Reindex();
        for (var sweep = 0; sweep < 4; sweep++)
        {
            var down = sweep % 2 == 0;
            foreach (var l in down ? layers.Skip(1) : Enumerable.Reverse(layers).Skip(1))
            {
                var keyed = l.Select(n => (n, k: (down ? preds[n] : succs[n]) is { Count: > 0 } nb ? nb.Average(x => index[x]) : index[n])).ToList();
                l.Clear();
                l.AddRange(keyed.OrderBy(x => x.k).Select(x => x.n));
                for (var i = 0; i < l.Count; i++) index[l[i]] = i;
            }
        }

        // Stack each layer, then pull nodes toward their neighbours' centres (two passes) keeping order and spacing.
        for (var li = 0; li < layers.Count; li++)
        {
            double y = 0;
            foreach (var n in layers[li]) { n.X = li * (n.W + LayerGap); n.Y = y; y += n.H + Gap; }
        }
        for (var pass = 0; pass < 2; pass++)
            foreach (var l in pass == 0 ? layers.Skip(1) : Enumerable.Reverse(layers))
            {
                foreach (var n in l)
                {
                    var nb = pass == 0 ? preds[n] : succs[n];
                    if (nb.Count > 0) n.Y = nb.Average(x => x.Y + x.H / 2) - n.H / 2;
                }
                for (var i = 1; i < l.Count; i++) l[i].Y = Math.Max(l[i].Y, l[i - 1].Y + l[i - 1].H + Gap);
            }
        Normalise(nodes);
    }

    // ── Model diagram: tables and relationships ─────────────────────────────

    public static Graph Diagram(Model model)
    {
        var tables = model.Tables.Where(t => !ModelInsight.IsAutoDateTable(t)).ToList();
        var rels = model.Relationships.OfType<SingleColumnRelationship>()
            .Where(r => tables.Contains(r.FromTable) && tables.Contains(r.ToTable)).ToList();
        var nodes = new Dictionary<Table, GNode>();
        foreach (var t in tables)
        {
            var p = t.Partitions.FirstOrDefault();
            var mode = p?.Mode is { } md && md != ModeType.Default ? md : model.DefaultMode;
            var n = nodes[t] = new GNode("t:" + t.Name, GKind.Table, t.Name,
                p?.Source is CalculatedPartitionSource ? "DAX table" : p?.Source is EntityPartitionSource ? "Direct Lake" : mode.ToString(),
                string.Join(" · ", new[]
                {
                    Plural(t.Columns.Count(c => c.Type != ColumnType.RowNumber), "column"),
                    t.Measures.Count > 0 ? Plural(t.Measures.Count, "measure") : null,
                }.OfType<string>()));
            foreach (var c in rels.SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c.Table == t).Distinct().OrderBy(c => c.Name))
                n.Rows.Add(new GRow(c.Name, c.DataType.ToString()));
            n.H = 60 + (n.Rows.Count > 0 ? n.Rows.Count * 24 + 8 : 0);
        }

        static string End(RelationshipEndCardinality c) => c == RelationshipEndCardinality.Many ? "*" : "1";
        // Filters flow from the "to" (one) side to the "from" (many) side.
        var edges = rels.Select(r => new GEdge(
            nodes[r.ToTable], nodes[r.FromTable],
            nodes[r.ToTable].Rows.FindIndex(x => x.Text == r.ToColumn.Name),
            nodes[r.FromTable].Rows.FindIndex(x => x.Text == r.FromColumn.Name),
            End(r.ToCardinality), End(r.FromCardinality),
            Dashed: !r.IsActive, Both: r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections)).ToList();

        var g = new Graph(nodes.Values.ToList(), edges, false);
        var linked = g.Nodes.Where(n => edges.Any(e => e.From == n || e.To == n)).ToList();
        LayerLayout(linked, edges, lineage: false);

        // Tables without relationships sit in a tidy grid underneath.
        var lonely = g.Nodes.Except(linked).OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var top = linked.Count > 0 ? linked.Max(n => n.Y + n.H) + 96 : Margin;
        var cols = Math.Max(3, linked.Select(n => n.Layer).Distinct().Count());
        double rowY = top, rowH = 0;
        for (var i = 0; i < lonely.Count; i++)
        {
            if (i > 0 && i % cols == 0) { rowY += rowH + Gap * 2; rowH = 0; }
            lonely[i].X = Margin + i % cols * (lonely[i].W + LayerGap);
            lonely[i].Y = rowY;
            rowH = Math.Max(rowH, lonely[i].H);
        }
        return g;
    }

    static void Normalise(IReadOnlyList<GNode> nodes)
    {
        if (nodes.Count == 0) return;
        var (mx, my) = (nodes.Min(n => n.X), nodes.Min(n => n.Y));
        foreach (var n in nodes) { n.X += Margin - mx; n.Y += Margin - my; }
    }
}
