using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Daxis.Core;

namespace Daxis.App;

/// <summary>
/// Pan/zoom canvas for a <see cref="Graph"/>. Cards ease in on show, hovering traces the connected path
/// (particles flow along it in the data/filter direction), cards can be dragged, double-click opens.
/// Everything is drawn in one Render pass; frames are only requested while something is moving.
/// </summary>
public sealed class GraphCanvas : Control
{
    public static readonly StyledProperty<Graph?> GraphProperty = AvaloniaProperty.Register<GraphCanvas, Graph?>(nameof(Graph));
    public Graph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }

    public event Action<GNode>? NodeOpened;

    const double Header = 60, RowH = 24, Dim = 0.14;

    sealed class Anim { public double X, Y, Appear, Focus = 1, Delay; }
    sealed record Texts(FormattedText Kicker, FormattedText Title, FormattedText Detail, FormattedText Big,
        List<(FormattedText Name, FormattedText Hint)> Rows);
    sealed record Palette(IBrush Bg, IBrush Card, IBrush Border, IBrush Strong, IBrush Fg, IBrush Muted, IBrush Faint,
        IBrush Brand, IBrush BrandText, IBrush BrandSoft, IBrush BrandBorder, FontFamily Ui, FontFamily Mono);

    readonly Dictionary<GNode, Anim> _anim = [];
    readonly Dictionary<GNode, Texts> _texts = [];
    readonly Dictionary<string, FormattedText> _labels = [];
    Dictionary<GNode, List<GEdge>> _in = [], _out = [];
    readonly HashSet<GNode> _lit = [];
    readonly HashSet<GEdge> _litEdges = [];
    Palette? _p;

    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last, _start;
    bool _framePending, _needsFit;

    double _zoom = 1, _zoomTarget = 1;
    double _detail = 1; // 1 = full card text, 0 = big titles only (zoomed out)
    Vector _pan, _panTarget, _panStart;
    (Point Screen, Point World)? _anchor;

    GNode? _hover, _selected, _dragNode;
    Point _pressAt;
    Vector _dragOffset;
    bool _panning, _moved;
    bool _userMoved; // the camera was moved by hand since the last fit, so rebuilds leave it alone

    public GraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => { _p = null; _texts.Clear(); _labels.Clear(); InvalidateVisual(); };
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Plays the entrance again (used when the page is shown).</summary>
    public void Replay()
    {
        if (Graph is null) return;
        Enter(Graph.Nodes, fromScratch: true);
        Fit(animate: false);
    }

    public void Fit(bool animate = true)
    {
        if (Graph is not { Nodes.Count: > 0 } g) return;
        if (Bounds.Width < 1) { _needsFit = true; return; }
        _userMoved = false;
        FitTo(g.Nodes, 1.05, animate);
    }

    void FitTo(IReadOnlyCollection<GNode> nodes, double maxZoom, bool animate = true)
    {
        double x0 = nodes.Min(n => n.X), y0 = nodes.Min(n => n.Y);
        double x1 = nodes.Max(n => n.X + n.W), y1 = nodes.Max(n => n.Y + n.H);
        var z = Math.Clamp(Math.Min((Bounds.Width - 96) / (x1 - x0), (Bounds.Height - 160) / (y1 - y0)), 0.12, maxZoom);
        _anchor = null;
        _zoomTarget = z;
        _panTarget = new Vector(Bounds.Width / 2 - (x0 + x1) / 2 * z, Bounds.Height / 2 - (y0 + y1) / 2 * z);
        if (!animate) { _zoom = z; _pan = _panTarget; }
        Kick();
    }

    /// <summary>Selects the first card whose title matches <paramref name="text"/> and frames it with everything it's traced to.</summary>
    public bool Find(string text)
    {
        if (Graph is null) return false;
        if (string.IsNullOrWhiteSpace(text))
        {
            _selected = null;
            UpdateFocus();
            return false;
        }
        var n = Graph.Nodes.FirstOrDefault(x => x.Title.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
             ?? Graph.Nodes.FirstOrDefault(x => x.Title.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (n is null) return false;
        _selected = n;
        UpdateFocus();
        FitTo(_lit, 1);
        return true;
    }

    // ── Graph changes ────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != GraphProperty) return;
        var old = _anim.ToDictionary(kv => kv.Key.Id, kv => kv.Value);
        var keep = _selected?.Id;
        _anim.Clear();
        _texts.Clear();
        _hover = _selected = _dragNode = null;
        _lit.Clear();
        _litEdges.Clear();
        if (Graph is not { } g) { InvalidateVisual(); return; }

        _in = g.Nodes.ToDictionary(n => n, _ => new List<GEdge>());
        _out = g.Nodes.ToDictionary(n => n, _ => new List<GEdge>());
        foreach (var e in g.Edges) { _in[e.To].Add(e); _out[e.From].Add(e); }

        // Nodes that survive a rebuild (same id) glide from where they were; new ones ease in.
        var fresh = new List<GNode>();
        foreach (var n in g.Nodes)
            if (old.TryGetValue(n.Id, out var a)) _anim[n] = new Anim { X = a.X, Y = a.Y, Appear = 1 };
            else fresh.Add(n);
        Enter(fresh, fromScratch: old.Count == 0);
        // A rebuild (e.g. another report analysed) keeps the trace and, unless the user moved the camera, reframes.
        _selected = g.Nodes.FirstOrDefault(n => n.Id == keep);
        UpdateFocus();
        if (old.Count == 0 || !_userMoved) Fit(animate: old.Count > 0);
    }

    void Enter(IEnumerable<GNode> nodes, bool fromScratch)
    {
        _start = _clock.Elapsed.TotalSeconds;
        var list = nodes.OrderBy(n => n.X).ThenBy(n => n.Y).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var n = list[i];
            _anim[n] = new Anim
            {
                // Lineage slides in from the left, the diagram settles inward from slightly outside.
                X = Graph!.Layered ? n.X - 48 : n.X + (n.X - 400) * 0.12,
                Y = Graph.Layered ? n.Y : n.Y + (n.Y - 300) * 0.12,
                Delay = fromScratch ? Math.Min(i * 0.022, 0.7) : 0.1,
            };
        }
        Kick();
    }

    // ── Animation loop ───────────────────────────────────────────────────────

    void Kick()
    {
        InvalidateVisual();
        if (_framePending || TopLevel.GetTopLevel(this) is not { } top) return;
        _framePending = true;
        top.RequestAnimationFrame(_ => Frame());
    }

    void Frame()
    {
        _framePending = false;
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        var k = 1 - Math.Exp(-dt * 10);   // exponential ease-out; frame-rate independent
        var kf = 1 - Math.Exp(-dt * 14);
        var busy = _litEdges.Count > 0;   // particles keep flowing while a path is traced

        foreach (var (n, a) in _anim)
        {
            if (now - _start < a.Delay) { busy = true; continue; }
            if (n != _dragNode)
            {
                a.X = Approach(a.X, n.X, k, 0.2, ref busy);
                a.Y = Approach(a.Y, n.Y, k, 0.2, ref busy);
            }
            a.Appear = Approach(a.Appear, 1, k * 0.9, 0.003, ref busy);
            a.Focus = Approach(a.Focus, _lit.Count == 0 || _lit.Contains(n) ? 1 : Dim, kf, 0.003, ref busy);
        }

        _detail = Approach(_detail, _zoomTarget >= 0.6 ? 1 : 0, kf, 0.01, ref busy);
        var zoomBefore = _zoom;
        _zoom = Approach(_zoom, _zoomTarget, kf, 0.0005, ref busy);
        if (_anchor is { } an) _pan = (Vector)an.Screen - (Vector)an.World * _zoom;
        else if (!_panning)
        {
            var d = _panTarget - _pan;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 0.3) { _pan += d * k; busy = true; }
            else _pan = _panTarget;
        }
        if (_anchor is not null && zoomBefore == _zoom) { _panTarget = _pan; _anchor = null; }

        InvalidateVisual();
        if (busy) Kick();
    }

    static double Approach(double v, double target, double k, double eps, ref bool busy)
    {
        if (Math.Abs(target - v) <= eps) return target;
        busy = true;
        return v + (target - v) * k;
    }

    // ── Focus (hover / selection tracing) ────────────────────────────────────

    void UpdateFocus()
    {
        _lit.Clear();
        _litEdges.Clear();
        if ((_hover ?? _selected) is { } n && Graph is { } g)
        {
            if (g.Layered)
            {
                // Whole upstream and downstream path.
                Walk(n, _in, e => e.From);
                Walk(n, _out, e => e.To);
            }
            else
            {
                _lit.Add(n);
                foreach (var e in _in[n].Concat(_out[n])) { _litEdges.Add(e); _lit.Add(e.From); _lit.Add(e.To); }
            }
        }
        Kick();
    }

    void Walk(GNode start, Dictionary<GNode, List<GEdge>> adj, Func<GEdge, GNode> next)
    {
        var stack = new Stack<GNode>([start]);
        var seen = new HashSet<GNode>();
        while (stack.TryPop(out var n))
        {
            if (!seen.Add(n)) continue;
            _lit.Add(n);
            foreach (var e in adj[n]) { _litEdges.Add(e); stack.Push(next(e)); }
        }
    }

    // ── Input ────────────────────────────────────────────────────────────────

    Point World(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    GNode? HitTest(Point screen)
    {
        if (Graph is null) return null;
        var w = World(screen);
        for (var i = Graph.Nodes.Count - 1; i >= 0; i--)
        {
            var n = Graph.Nodes[i];
            if (_anim.TryGetValue(n, out var a) && new Rect(a.X, a.Y, n.W, n.H).Contains(w)) return n;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed && !p.Properties.IsMiddleButtonPressed) return;
        Focus();
        var node = HitTest(p.Position);
        if (e.ClickCount == 2)
        {
            if (node is not null) NodeOpened?.Invoke(node);
            else Fit();
            e.Handled = true;
            return;
        }
        e.Pointer.Capture(this);
        _pressAt = p.Position;
        _moved = false;
        _anchor = null;
        if (node is not null && p.Properties.IsLeftButtonPressed)
        {
            _dragNode = node;
            _dragOffset = World(p.Position) - new Point(_anim[node].X, _anim[node].Y);
        }
        else
        {
            _panning = true;
            _panStart = _pan;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _moved |= Math.Abs(pos.X - _pressAt.X) + Math.Abs(pos.Y - _pressAt.Y) > 3;
        if (_dragNode is { } n)
        {
            if (!_moved) return;
            var w = World(pos) - _dragOffset;
            (n.X, n.Y) = (w.X, w.Y);
            (_anim[n].X, _anim[n].Y) = (w.X, w.Y);
            InvalidateVisual();
        }
        else if (_panning)
        {
            _userMoved |= _moved;
            _pan = _panTarget = _panStart + (pos - _pressAt);
            InvalidateVisual();
        }
        else
        {
            var h = HitTest(pos);
            if (h == _hover) return;
            _hover = h;
            Cursor = h is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            UpdateFocus();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_moved)
        {
            _selected = HitTest(e.GetPosition(this));
            UpdateFocus();
        }
        _dragNode = null;
        _panning = false;
        Cursor = _hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        UpdateFocus();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var pos = e.GetPosition(this);
        _anchor = (pos, World(pos)); // keep the point under the cursor fixed while the zoom eases
        _userMoved = true;
        _zoomTarget = Math.Clamp(_zoomTarget * Math.Pow(1.2, e.Delta.Y), 0.1, 2.5);
        Kick();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.F or Key.Home) { Fit(); e.Handled = true; }
        if (e.Key == Key.Escape) { _selected = null; UpdateFocus(); e.Handled = true; }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_needsFit && e.NewSize.Width > 0) { _needsFit = false; Fit(animate: false); }
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    Palette P => _p ??= new Palette(B("Bg1"), B("Bg2"), B("Border"), B("BorderStrong"), B("Fg"), B("FgMuted"), B("FgFaint"),
        B("Brand"), B("BrandText"), B("BrandSoft"), B("BrandBorder"), F("UiFont"), F("MonoFont"));

    IBrush B(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
    FontFamily F(string key) => this.TryFindResource(key, out var v) && v is FontFamily f ? f : FontFamily.Default;

    FormattedText Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal, bool mono = false, double maxWidth = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(mono ? P.Mono : P.Ui, FontStyle.Normal, weight), size, brush);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    Texts TextsFor(GNode n) => _texts.TryGetValue(n, out var t) ? t : _texts[n] = new Texts(
        Text(n.Kicker.ToUpperInvariant(), 10, P.Faint, FontWeight.Medium, maxWidth: n.W - 56),
        Text(n.Title, 13, P.Fg, FontWeight.Medium, maxWidth: n.W - 56),
        Text(n.Detail, 11, P.Muted, mono: true, maxWidth: n.W - 56),
        Text(n.Title, 20, P.Fg, FontWeight.Medium, maxWidth: n.W - 56),
        n.Rows.Select(r => (Text(r.Text, 12, P.Fg, maxWidth: n.W - 100), Text(r.Hint.ToLowerInvariant(), 10.5, P.Faint, mono: true))).ToList());

    public override void Render(DrawingContext ctx)
    {
        var p = P;
        ctx.FillRectangle(p.Bg, new Rect(Bounds.Size));
        DrawDots(ctx, p);
        if (Graph is not { } g) return;

        using (ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan)))
        {
            var t = _clock.Elapsed.TotalSeconds;
            foreach (var e in g.Edges) if (!_litEdges.Contains(e)) DrawEdge(ctx, p, e, false, t);
            foreach (var e in _litEdges) DrawEdge(ctx, p, e, true, t);
            foreach (var n in g.Nodes) DrawNode(ctx, p, n);
        }
    }

    void DrawDots(DrawingContext ctx, Palette p)
    {
        var step = 24 * _zoom;
        while (step < 14) step *= 2;
        var size = Math.Clamp(1.6 * _zoom, 1.2, 1.8);
        // One tiled brush instead of thousands of rectangles per frame.
        var tile = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, step, step)) },
                new GeometryDrawing { Brush = p.Strong, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
            },
        };
        var brush = new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(_pan.X % step, _pan.Y % step, step, step, RelativeUnit.Absolute),
        };
        ctx.FillRectangle(brush, new Rect(Bounds.Size));
    }

    (Point A, Point C1, Point C2, Point B) Curve(GEdge e, bool layered)
    {
        Anim fa = _anim[e.From], ta = _anim[e.To];
        double RowY(GNode n, Anim a, int row) => a.Y + (row >= 0 ? Header + 4 + row * RowH + RowH / 2 : layered ? n.H / 2 : Header / 2);
        double ay = RowY(e.From, fa, e.FromRow), by = RowY(e.To, ta, e.ToRow);
        double fc = fa.X + e.From.W / 2, tc = ta.X + e.To.W / 2;
        int sa, sb; // outward direction at each end: +1 right side, -1 left side
        if (layered || tc > fc + e.From.W * 0.6) (sa, sb) = (1, -1);
        else if (tc < fc - e.From.W * 0.6) (sa, sb) = (-1, 1);
        else (sa, sb) = (1, 1); // stacked: loop around the right side
        var a = new Point(sa > 0 ? fa.X + e.From.W : fa.X, ay);
        var b = new Point(sb > 0 ? ta.X + e.To.W : ta.X, by);
        var d = Math.Max(48, Math.Abs(b.X - a.X) * 0.5);
        return (a, new Point(a.X + sa * d, a.Y), new Point(b.X + sb * d, b.Y), b);
    }

    static Point Bezier((Point A, Point C1, Point C2, Point B) c, double t)
    {
        var u = 1 - t;
        return (Point)((Vector)c.A * (u * u * u) + (Vector)c.C1 * (3 * u * u * t) + (Vector)c.C2 * (3 * u * t * t) + (Vector)c.B * (t * t * t));
    }

    void DrawEdge(DrawingContext ctx, Palette p, GEdge e, bool lit, double time)
    {
        Anim fa = _anim[e.From], ta = _anim[e.To];
        var op = Math.Min(fa.Appear, ta.Appear) * (lit ? 1 : Math.Min(fa.Focus, ta.Focus));
        if (op < 0.02) return;
        var c = Curve(e, Graph!.Layered);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(c.A, false);
            g.CubicBezierTo(c.C1, c.C2, c.B);
            g.EndFigure(false);
        }
        using (ctx.PushOpacity(op))
        {
            var pen = new Pen(lit ? p.BrandText : p.Strong, (lit ? 1.8 : 1.2) / Math.Max(_zoom, 0.5),
                e.Dashed ? new DashStyle([4, 4], 0) : null, PenLineCap.Round);
            ctx.DrawGeometry(null, pen, geo);
            ctx.DrawEllipse(lit ? p.BrandText : p.Strong, null, c.A, 2.5, 2.5);
            ctx.DrawEllipse(lit ? p.BrandText : p.Strong, null, c.B, 2.5, 2.5);

            if (e.FromLabel is { } fl) DrawLabel(ctx, p, fl, c.A, c.C1, lit);
            if (e.ToLabel is { } tl) DrawLabel(ctx, p, tl, c.B, c.C2, lit);

            if (!lit) return;
            // Particles travel in the flow direction (both ways for bidirectional filters).
            for (var i = 0; i < 3; i++)
            {
                var t = (time * 0.45 + i / 3.0) % 1;
                ctx.DrawEllipse(p.Brand, null, Bezier(c, t), 3, 3);
                if (e.Both) ctx.DrawEllipse(p.Brand, null, Bezier(c, 1 - t), 2.2, 2.2);
            }
        }
    }

    void DrawLabel(DrawingContext ctx, Palette p, string text, Point at, Point toward, bool lit)
    {
        var key = text + (lit ? "!" : "");
        var ft = _labels.TryGetValue(key, out var f) ? f : _labels[key] = Text(text, 11, lit ? p.BrandText : p.Muted, FontWeight.Medium, mono: true);
        var dir = toward.X >= at.X ? 1 : -1;
        ctx.DrawText(ft, new Point(dir > 0 ? at.X + 7 : at.X - 7 - ft.Width, at.Y - ft.Height - 1));
    }

    void DrawNode(DrawingContext ctx, Palette p, GNode n)
    {
        var a = _anim[n];
        var op = a.Appear * a.Focus;
        if (op < 0.01) return;
        var hot = n == _hover || n == _selected;
        var s = 0.94 + 0.06 * a.Appear;
        var cx = a.X + n.W / 2;
        var cy = a.Y + n.H / 2;
        using var _ = ctx.PushOpacity(op);
        using var __ = ctx.PushTransform(Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy));

        var r = new Rect(a.X, a.Y, n.W, n.H);
        ctx.DrawRectangle(p.Card, new Pen(hot ? p.BrandBorder : p.Border, hot ? 1.5 : 1), r, 8, 8);
        if (hot) ctx.DrawRectangle(p.BrandSoft, null, r, 8, 8);

        var tx = TextsFor(n);
        var icon = n.Kind switch
        {
            GKind.Source => Icons.Lakehouse,
            GKind.Query => Icons.Query,
            GKind.Report => Icons.Report,
            GKind.Model => Icons.Model,
            _ => Icons.Table,
        };
        using (ctx.PushTransform(Matrix.CreateScale(18 / 24.0, 18 / 24.0) * Matrix.CreateTranslation(a.X + 14, a.Y + Header / 2 - 9)))
            ctx.DrawGeometry(null, new Pen(hot ? p.BrandText : p.Muted, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), icon);

        // Semantic zoom: zoomed out, detail fades and the title grows so the overview stays legible.
        var detail = _detail;
        if (detail < 1)
            using (ctx.PushOpacity(1 - detail))
                ctx.DrawText(tx.Big, new Point(a.X + 44, a.Y + (Header - tx.Big.Height) / 2));
        if (tx.Rows.Count > 0)
            ctx.DrawLine(new Pen(p.Border, 1), new Point(a.X, a.Y + Header), new Point(a.X + n.W, a.Y + Header));
        for (var i = 0; i < tx.Rows.Count; i++)
            ctx.DrawEllipse(null, new Pen(p.BrandText, 1.3), new Point(a.X + 22, a.Y + Header + 4 + i * RowH + RowH / 2), 3.2, 3.2);
        if (detail <= 0) return;

        using var ___ = ctx.PushOpacity(detail);
        ctx.DrawText(tx.Kicker, new Point(a.X + 44, a.Y + 10));
        ctx.DrawText(tx.Title, new Point(a.X + 44, a.Y + 23));
        ctx.DrawText(tx.Detail, new Point(a.X + 44, a.Y + 41));
        for (var i = 0; i < tx.Rows.Count; i++)
        {
            var y = a.Y + Header + 4 + i * RowH;
            var (name, hint) = tx.Rows[i];
            ctx.DrawText(name, new Point(a.X + 44, y + (RowH - name.Height) / 2));
            ctx.DrawText(hint, new Point(a.X + n.W - 14 - hint.Width, y + (RowH - hint.Height) / 2));
        }
    }
}
