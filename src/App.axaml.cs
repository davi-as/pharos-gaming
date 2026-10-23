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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
