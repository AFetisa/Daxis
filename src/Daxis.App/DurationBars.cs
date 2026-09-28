using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Daxis.Core;

namespace Daxis.App;

/// <summary>
/// Refresh durations as bars, oldest left. Completed runs use soft brand (latest in full brand), failures use danger.
/// Bars grow in when the data arrives.
/// </summary>
public sealed class DurationBars : Control
{
    public static readonly StyledProperty<IReadOnlyList<RefreshRun>?> RunsProperty =
        AvaloniaProperty.Register<DurationBars, IReadOnlyList<RefreshRun>?>(nameof(Runs));

    public IReadOnlyList<RefreshRun>? Runs { get => GetValue(RunsProperty); set => SetValue(RunsProperty, value); }

    double _grow = 1, _last;
    bool _pending;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public DurationBars() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RunsProperty) return;
        _grow = 0;
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
            _grow += (1 - _grow) * (1 - Math.Exp(-Math.Clamp(now - _last, 0, 0.05) * 7));
            if (1 - _grow < 0.002) _grow = 1;
            else Kick();
            InvalidateVisual();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_grow < 1) Kick();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 48 : Height);

    public override void Render(DrawingContext ctx)
    {
        var runs = Runs?.Where(r => r.Duration is not null).Reverse().ToList();
        if (runs is not { Count: > 0 }) return;
        var max = runs.Max(r => r.Duration!.Value.TotalSeconds);
        var slot = Bounds.Width / runs.Count;
        var w = Math.Clamp(slot * 0.62, 2, 14);
        for (var i = 0; i < runs.Count; i++)
        {
            var h = Math.Max(2, Bounds.Height * runs[i].Duration!.Value.TotalSeconds / Math.Max(max, 1) * _grow);
            var brush = runs[i].Failed ? Brush("Danger") : i == runs.Count - 1 ? Brush("Brand") : Brush("BrandBorder");
            ctx.DrawRectangle(brush, null, new Rect(i * slot + (slot - w) / 2, Bounds.Height - h, w, h), 2, 2);
        }
    }

    IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
}
