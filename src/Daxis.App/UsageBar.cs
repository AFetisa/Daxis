using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Daxis.App;

/// <summary>
/// Rounded share bar: <see cref="Primary"/> in brand over <see cref="Secondary"/> in soft brand, on a neutral track.
/// Grows from zero whenever it's shown and eases to new values.
/// </summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double> PrimaryProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Primary));
    public static readonly StyledProperty<double> SecondaryProperty = AvaloniaProperty.Register<UsageBar, double>(nameof(Secondary));

    public double Primary { get => GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }
    public double Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }

    double _p, _s, _last;
    bool _pending;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public UsageBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PrimaryProperty || change.Property == SecondaryProperty) Kick();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _p = _s = 0;
        Kick();
    }

    void Kick()
    {
        InvalidateVisual();
        if (_pending || TopLevel.GetTopLevel(this) is not { } top) return;
        _pending = true;
        _last = _clock.Elapsed.TotalSeconds;
        top.RequestAnimationFrame(_ => Frame());
    }

    void Frame()
    {
        _pending = false;
        var now = _clock.Elapsed.TotalSeconds;
        var k = 1 - Math.Exp(-Math.Clamp(now - _last, 0, 0.05) * 6);
        _p += (Primary - _p) * k;
        _s += (Secondary - _s) * k;
        if (Math.Abs(Primary - _p) < 0.001 && Math.Abs(Secondary - _s) < 0.001) (_p, _s) = (Primary, Secondary);
        else Kick();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 8 : Height);

    public override void Render(DrawingContext ctx)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        var r = h / 2;
        ctx.DrawRectangle(Brush("Bg3"), null, new Rect(0, 0, w, h), r, r);
        if (_p + _s > 0.001) ctx.DrawRectangle(Brush("BrandBorder"), null, new Rect(0, 0, Math.Max(h, w * Math.Min(1, _p + _s)), h), r, r);
        if (_p > 0.001) ctx.DrawRectangle(Brush("Brand"), null, new Rect(0, 0, Math.Max(h, w * Math.Min(1, _p)), h), r, r);
    }

    IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
}
