using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace PharosGaming;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Cor da paleta do Pharos no tema atual (as cores ficam em dicionários por tema, fora do Resources comum).</summary>
    public static IBrush Brush(string key) =>
        Current!.TryGetResource(key, Current.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    public static void ApplyTheme(string theme) =>
        Current!.RequestedThemeVariant = theme switch { "light" => Avalonia.Styling.ThemeVariant.Light, "dark" => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default };

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(AppSettings.Current.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            // --tv: só o modo TV (o controle abre assim, pela ação "Pharos na TV" do daemon do console)
            desktop.MainWindow = desktop.Args?.Contains("--tv") == true ? new TvWindow() : new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
