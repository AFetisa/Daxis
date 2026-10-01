using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Daxis.App;

/// <summary>Stroked 24×24 line icon rendered at a constant 1.6px stroke, coloured by inherited Foreground.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon() => AffectsRender<Icon>(DataProperty, ForegroundProperty);

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext ctx)
    {
        if (Data is null || Foreground is null) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
            ctx.DrawGeometry(null, new Pen(Foreground, 1.6 / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Data);
    }
}

/// <summary>Hand-drawn 24×24 stroke geometries, Lucide-style.</summary>
public static class Icons
{
    static Geometry P(string d) => Geometry.Parse(d);

    public static readonly Geometry Model = P("M12,3 L20,7.5 L20,16.5 L12,21 L4,16.5 L4,7.5 Z M4,7.5 L12,12 L20,7.5 M12,12 L12,21");
    public static readonly Geometry Notebook = P("M6,3 H18 A1,1 0 0 1 19,4 V20 A1,1 0 0 1 18,21 H6 A1,1 0 0 1 5,20 V4 A1,1 0 0 1 6,3 Z M9,3 V21 M12.5,8 H16 M12.5,12 H16");
    public static readonly Geometry Lakehouse = P("M4,6 C4,3.8 20,3.8 20,6 C20,8.2 4,8.2 4,6 Z M4,6 V18 C4,20.2 20,20.2 20,18 V6 M4,12 C4,14.2 20,14.2 20,12");
    public static readonly Geometry Table = P("M4,5 H20 V19 H4 Z M4,10 H20 M4,14.5 H20 M10,10 V19");
    public static readonly Geometry Measure = P("M17,5 H7 L12.5,12 L7,19 H17");
    public static readonly Geometry Column = P("M5,4 H19 V20 H5 Z M5,9 H19 M12,9 V20");
    public static readonly Geometry CalcColumn = P("M5,4 H19 V20 H5 Z M5,9 H19 M9,14.5 H15 M12,11.5 V17.5");
    public static readonly Geometry Folder = P("M3,7 A1.5,1.5 0 0 1 4.5,5.5 H9.5 L11.5,7.5 H19.5 A1.5,1.5 0 0 1 21,9 V17.5 A1.5,1.5 0 0 1 19.5,19 H4.5 A1.5,1.5 0 0 1 3,17.5 Z");
    public static readonly Geometry File = P("M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19");
    public static readonly Geometry Search = P("M10.5,4 A6.5,6.5 0 1 1 10.49,4 Z M15.5,15.5 L20,20");
    public static readonly Geometry Play = P("M8,5 L19,12 L8,19 Z");
    public static readonly Geometry Plus = P("M12,5 V19 M5,12 H19");
    public static readonly Geometry Close = P("M7,7 L17,17 M17,7 L7,17");
    public static readonly Geometry Trash = P("M4,7 H20 M9,7 V4.5 H15 V7 M6.5,7 L7.5,20 H16.5 L17.5,7 M10,11 V16 M14,11 V16");
    public static readonly Geometry Format = P("M4,6 H20 M8,10 H20 M8,14 H17 M4,18 H13");
    public static readonly Geometry Refresh = P("M19.5,12 A7.5,7.5 0 1 1 17.3,6.7 M19.5,4.5 V9 H15");
    public static readonly Geometry External = P("M14,4 H20 V10 M20,4 L11,13 M18,14 V20 H4 V6 H10");
    public static readonly Geometry Copy = P("M9,9 H20 V20 H9 Z M15,9 V4 H4 V15 H9");
    public static readonly Geometry Up = P("M12,19 V5 M6,11 L12,5 L18,11");
    public static readonly Geometry Moon = P("M20,14.5 A8,8 0 1 1 9.5,4 A6.5,6.5 0 0 0 20,14.5 Z");
    public static readonly Geometry Sun = P("M12,8 A4,4 0 1 1 11.99,8 Z M12,2.5 V4.5 M12,19.5 V21.5 M2.5,12 H4.5 M19.5,12 H21.5 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M5.3,18.7 L6.7,17.3 M17.3,6.7 L18.7,5.3");
    public static readonly Geometry Query = P("M4,5 H20 L14,12.5 V18.5 L10,20 V12.5 Z");
    public static readonly Geometry Param = P("M4,7 H20 M4,17 H20 M9,4.5 V9.5 M15,14.5 V19.5");
    public static readonly Geometry Check = P("M5,12.5 L10,17 L19,7");
    public static readonly Geometry Report = P("M4,20 H20 M7,20 V12 M12,20 V5 M17,20 V9");
    public static readonly Geometry Diagram = P("M4,4 H10 V9 H4 Z M14,15 H20 V20 H14 Z M14,4 H20 V9 H14 Z M7,9 V17.5 H14 M17,9 V15");
    public static readonly Geometry Workspace = P("M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z");
    public static readonly Geometry Branch = P("M6,3 V15 M6,15 A3,3 0 1 1 5.99,15 Z M18,3 A3,3 0 1 1 17.99,3 Z M18,9 C18,13.5 11,14.5 6,15");
    public static readonly Geometry Download = P("M12,4 V15 M7,10 L12,15 L17,10 M5,19 H19");
    public static readonly Geometry Upload = P("M12,15 V4 M7,9 L12,4 L17,9 M5,19 H19");
    public static readonly Geometry Shield = P("M12,3 L19,6 V11 C19,15.5 16,19 12,21 C8,19 5,15.5 5,11 V6 Z M9,12 L11,14 L15,10");
    public static readonly Geometry Gauge = P("M4,17 A8,8 0 1 1 20,17 M12,17 L16,10 M4,17 H6 M18,17 H20");
    public static readonly Geometry Warn = P("M12,4 L21,19 H3 Z M12,10 V14 M12,16.5 V17");
    public static readonly Geometry SignOut = P("M10,5 H5 V19 H10 M15,8 L19,12 L15,16 M19,12 H9");
}
