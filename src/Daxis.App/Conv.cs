using Avalonia.Data.Converters;

namespace Daxis.App;

public static class Conv
{
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(v => v == 0);

    /// <summary>Page switch: true when the bound index equals ConverterParameter.</summary>
    public static readonly IValueConverter Is = new FuncValueConverter<int, object?, bool>((v, p) => v.ToString() == p?.ToString());

    /// <summary>Review badge colour: Added → brand, Deleted → danger, Modified → muted.</summary>
    public static readonly IValueConverter ActionBrush = new FuncValueConverter<string, Avalonia.Media.IBrush?>(a =>
    {
        var app = Avalonia.Application.Current!;
        var key = a switch { "Added" => "BrandText", "Deleted" => "Danger", _ => "FgMuted" };
        return app.TryGetResource(key, app.ActualThemeVariant, out var b) ? b as Avalonia.Media.IBrush : null;
    });

    public static readonly IValueConverter Bytes = new FuncValueConverter<long, string>(Format);

    internal static string Format(long b) => b switch
    {
        0 => "",
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

public static class Web
{
    /// <summary>The product's own pages, the only non-portal links the app opens.</summary>
    static readonly string[] Product = ["https://github.com/AFetisa/daxis", "https://www.portable-labs.com"];

    /// <summary>Opens a link in the browser. Only https Fabric / Power BI portal URLs and the product pages reach the OS launcher.</summary>
    public static void Open(Avalonia.Visual from, string? url)
    {
        if (Daxis.Core.FabricClient.IsPortalLink(url) || Product.Contains(url))
            Avalonia.Controls.TopLevel.GetTopLevel(from)?.Launcher.LaunchUriAsync(new Uri(url!));
    }
}
