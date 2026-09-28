using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Daxis.Core;

namespace Daxis.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = Settings.Load();
        RequestedThemeVariant = settings.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(settings) };
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class Settings
{
    public string? Workspace { get; set; }
    public bool Light { get; set; }

    static string FilePath => Path.Combine(AppPaths.Data, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
}
