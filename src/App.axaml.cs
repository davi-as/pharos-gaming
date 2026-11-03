using Avalonia;
using Avalonia.Controls;
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

    private TvWindow? _overlay; // --tv --hidden: a janela existe sem aparecer

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(AppSettings.Current.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            // --tv: só o menu, como overlay residente (o Guide + View do daemon do console abre e esconde com SIGUSR1).
            // --hidden (autostart do login): já mede o jogo desde o começo, e o 1º Guide + View só mostra
            if (args.Contains("--tv") && args.Contains("--hidden"))
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _overlay = new TvWindow(overlay: true);
            }
            else desktop.MainWindow = args.Contains("--tv") ? new TvWindow(overlay: true) : new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
