using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Daxis.Core;

namespace Daxis.App;

/// <summary>One tile: size by <see cref="Value"/>; <see cref="Tag"/> travels back in <see cref="TreemapView.TileClicked"/>.</summary>
public sealed record TreeTile(string Label, long Value, string Detail, object? Tag = null);

/// <summary>
/// Squarified treemap. Bigger tiles carry more brand tint so the heavy hitters read first; tiles grow in,
/// staggered by size. Hover shows the detail, click raises <see cref="TileClicked"/>.
/// </summary>
public sealed class TreemapView : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreeTile>?> TilesProperty =
        AvaloniaProperty.Register<TreemapView, IReadOnlyList<TreeTile>?>(nameof(Tiles));

    public IReadOnlyList<TreeTile>? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }
    public event Action<TreeTile>? TileClicked;

    List<TreeTile> _tiles = [];
    Box[] _boxes = [];
    Size _laidOut;
    int _hover = -1;
    double _grow = 1, _last, _start;
    bool _pending;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public TreemapView()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TilesProperty) return;
        _tiles = (Tiles ?? []).Where(t => t.Value > 0).OrderByDescending(t => t.Value).ToList();
        _laidOut = default;
        _grow = 0;
        _start = _clock.Elapsed.TotalSeconds;
        Kick();
    }

    void Kick()
    {
        InvalidateVisual();
        if (_pending || TopLevel.GetTopLevel(this) is not { } top) return;
        _pending = true;
        _last = _clock.Elapsed.TotalSeconds;
        top.RequestAnimationFrame(_ =>
        {
            _pending = false;
            var now = _clock.Elapsed.TotalSeconds;
            _grow = Math.Min(1, _grow + Math.Clamp(now - _last, 0, 0.05) / 0.7); // 0.7s timeline; tiles stagger within it
            if (_grow < 1) Kick();
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_grow < 1) Kick();
    }

    void Layout()
    {
        if (_laidOut == Bounds.Size) return;
        _laidOut = Bounds.Size;
        _boxes = TreemapLayout.Squarify(_tiles.Select(t => (double)t.Value).ToList(), new Box(0, 0, Bounds.Width, Bounds.Height));
    }

    int Hit(Point p)
    {
        for (var i = 0; i < _boxes.Length; i++)
            if (_boxes[i] is var b && p.X >= b.X && p.X < b.X + b.W && p.Y >= b.Y && p.Y < b.Y + b.H) return i;
        return -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var h = Hit(e.GetPosition(this));
        if (h == _hover) return;
        _hover = h;
        Cursor = h < 0 ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, h < 0 ? null : $"{_tiles[h].Label}\n{_tiles[h].Detail}");
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Hit(e.GetPosition(this)) is var h and >= 0) TileClicked?.Invoke(_tiles[h]);
    }

    public override void Render(DrawingContext ctx)
    {
        if (_tiles.Count == 0) return;
        Layout();
        IBrush B(string k) => this.TryFindResource(k, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
        var ui = this.TryFindResource("UiFont", out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        var mono = this.TryFindResource("MonoFont", out var m) && m is FontFamily mf ? mf : FontFamily.Default;
        var brand = B("Brand");
        var max = (double)_tiles[0].Value;

        for (var i = 0; i < _tiles.Count; i++)
        {
            var box = _boxes[i];
            if (box.W < 1 || box.H < 1) continue;
            // Largest tiles land first; each eases in over the back half of its slot.
            var p = Math.Clamp((_grow - 0.4 * i / _tiles.Count) / 0.6, 0, 1);
            var e = 1 - Math.Pow(1 - p, 3);
            if (e <= 0) continue;
            var r = new Rect(box.X + 2, box.Y + 2, Math.Max(0, box.W - 4), Math.Max(0, box.H - 4));
            var c = r.Center;
            using var _ = ctx.PushOpacity(e);
            using var __ = ctx.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateScale(0.9 + 0.1 * e, 0.9 + 0.1 * e) * Matrix.CreateTranslation(c));

            ctx.DrawRectangle(B("Bg2"), new Pen(i == _hover ? B("BrandBorder") : B("Border"), i == _hover ? 1.5 : 1), r, 6, 6);
            using (ctx.PushOpacity(0.12 + 0.5 * Math.Sqrt(_tiles[i].Value / max)))
                ctx.DrawRectangle(brand, null, r, 6, 6);

            if (r.Width < 56 || r.Height < 26) continue;
            var label = Text(_tiles[i].Label, 12.5, ui, B("Fg"), FontWeight.Medium, r.Width - 16);
            ctx.DrawText(label, new Point(r.X + 8, r.Y + 6));
            if (r.Height >= 44)
                ctx.DrawText(Text(ModelStorage.Format(_tiles[i].Value), 11, mono, B("FgMuted"), FontWeight.Normal, r.Width - 16),
                    new Point(r.X + 8, r.Y + 8 + label.Height));
        }
    }

    static FormattedText Text(string s, double size, FontFamily font, IBrush brush, FontWeight weight, double max)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = Math.Max(1, max), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };
        return ft;
    }
}
