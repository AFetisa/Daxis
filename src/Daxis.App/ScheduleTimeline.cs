using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Daxis.App;

/// <summary>One model on the 24-hour timeline: scheduled starts (viewer's local time) and its typical duration.</summary>
public sealed record TimelineRow(string Name, IReadOnlyList<TimeSpan> Starts, TimeSpan Duration, bool Failing);

/// <summary>
/// 24-hour schedule chart: a row per model with a bar per scheduled start, as long as the model's typical refresh.
/// A concurrency strip on top shows how many refreshes overlap; the brand line marks now. Bars grow in on show.
/// </summary>
public sealed class ScheduleTimeline : Control
{
    public static readonly StyledProperty<IReadOnlyList<TimelineRow>?> RowsProperty =
        AvaloniaProperty.Register<ScheduleTimeline, IReadOnlyList<TimelineRow>?>(nameof(Rows));

    public IReadOnlyList<TimelineRow>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    const double Label = 200, RowH = 26, Axis = 22, Strip = 28;

    double _grow = 1, _last;
    bool _pending;
    int _hoverRow = -1;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public ScheduleTimeline() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RowsProperty) return;
        _grow = 0;
        InvalidateMeasure();
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
            _grow += (1 - _grow) * (1 - Math.Exp(-Math.Clamp(now - _last, 0, 0.05) * 6));
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

    protected override Size MeasureOverride(Size availableSize) => new(0, Strip + Axis + Math.Max(1, Rows?.Count ?? 0) * RowH + 8);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var y = e.GetPosition(this).Y - Strip - Axis;
        var row = y < 0 ? -1 : (int)(y / RowH);
        if (row >= (Rows?.Count ?? 0)) row = -1;
        if (row == _hoverRow) return;
        _hoverRow = row;
        ToolTip.SetTip(this, row < 0 ? null : Tip(Rows![row]));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverRow = -1;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }

    static string Tip(TimelineRow r) =>
        $"{r.Name}\n{string.Join(", ", r.Starts.Select(s => s.ToString(@"hh\:mm")))} · typically {Daxis.Core.RefreshHealth.Dur(r.Duration)}" +
        (r.Failing ? "\nLast refresh failed" : "");

    public override void Render(DrawingContext ctx)
    {
        if (Rows is not { } rows) return;
        IBrush B(string k) => this.TryFindResource(k, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
        var ui = this.TryFindResource("UiFont", out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        FormattedText T(string s, double size, IBrush brush, double max = 0)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(ui), size, brush);
            if (max > 0) { ft.MaxTextWidth = max; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
            return ft;
        }

        var w = Math.Max(1, Bounds.Width - Label - 8);
        double X(TimeSpan t) => Label + w * t.TotalHours / 24;
        var top = Strip + Axis;

        // Hour grid and labels
        var grid = new Pen(B("Border"), 1);
        for (var h = 0; h <= 24; h += 3)
        {
            var x = X(TimeSpan.FromHours(h));
            ctx.DrawLine(grid, new Point(x, Strip + Axis - 4), new Point(x, top + rows.Count * RowH));
            if (h < 24)
            {
                var lbl = T($"{h:00}:00", 10.5, B("FgFaint"));
                ctx.DrawText(lbl, new Point(x + 3, Strip + 4));
            }
        }

        // Concurrency strip: overlapping refreshes per 10 minutes
        var buckets = new int[144];
        foreach (var r in rows)
            foreach (var s in r.Starts)
            {
                var n = Math.Max(1, (int)Math.Ceiling(r.Duration.TotalMinutes / 10));
                for (var i = 0; i < n; i++) buckets[((int)(s.TotalMinutes / 10) + i) % 144]++;
            }
        var peak = Math.Max(1, buckets.Max());
        for (var i = 0; i < 144; i++)
        {
            if (buckets[i] == 0) continue;
            var h = (Strip - 6) * buckets[i] / peak * _grow;
            ctx.DrawRectangle(buckets[i] > 1 ? B("Warning") : B("BrandBorder"), null,
                new Rect(X(TimeSpan.FromMinutes(i * 10)), Strip - 2 - h, Math.Max(1, w / 144 - 1), h), 1, 1);
        }

        // Rows
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var y = top + i * RowH;
            if (i == _hoverRow) ctx.DrawRectangle(B("Bg3"), null, new Rect(0, y, Bounds.Width, RowH), 4, 4);
            var name = T(r.Name, 12, r.Failing ? B("Danger") : B("Fg"), Label - 16);
            ctx.DrawText(name, new Point(8, y + (RowH - name.Height) / 2));
            var len = w * Math.Max(r.Duration.TotalHours, 1 / 60.0) / 24 * _grow;
            foreach (var s in r.Starts)
            {
                var x = X(s);
                var bar = r.Failing ? B("Danger") : B("Brand");
                var first = Math.Min(len, Label + w - x);
                ctx.DrawRectangle(bar, null, new Rect(x, y + 7, Math.Max(3, first), RowH - 14), 3, 3);
                if (len > first) ctx.DrawRectangle(bar, null, new Rect(Label, y + 7, len - first, RowH - 14), 3, 3); // past midnight
            }
        }

        // Now
        var nowX = X(DateTime.Now.TimeOfDay);
        ctx.DrawLine(new Pen(B("BrandText"), 1.5, new DashStyle([3, 3], 0)), new Point(nowX, Strip + Axis - 6), new Point(nowX, top + rows.Count * RowH));
    }
}
