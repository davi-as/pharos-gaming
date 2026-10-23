using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(PharosGaming.Tests.TestApp))]

namespace PharosGaming.Tests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // configurações numa pasta temporária: os testes nunca tocam em ~/.config/pharos-gaming
        Environment.SetEnvironmentVariable("PHAROS_CONFIG_DIR", Directory.CreateTempSubdirectory("pharos-test").FullName);
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>Abre a janela e visita cada página do menu: nenhuma pode lançar exceção ao ser criada ou ao receber leituras.</summary>
public class UiTests
{
    [AvaloniaFact]
    public void EveryPageOpens()
    {
        var w = new MainWindow();
        w.Show();
        var nav = w.FindControl<StackPanel>("Nav")!.Children.OfType<RadioButton>().ToList();
        Assert.Equal(21, nav.Count); // as 21 páginas do PHAROS GAMING para Windows
        foreach (var b in nav)
        {
            b.IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Live.Step();
            Live.Step(); // a segunda leitura já tem carga de CPU (diferença entre leituras)
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        var pages = w.FindControl<Panel>("Pages")!.Children;
        Assert.Equal(nav.Count, pages.Count);
        Assert.Single(pages, p => p.IsVisible);
        w.Close();
    }
}

public class ThemeTests
{
    [AvaloniaFact]
    public void ThemeButton_CyclesAndChangesColors()
    {
        var w = new MainWindow();
        w.Show();
        var btn = w.FindControl<Button>("BtnTheme")!;
        AppSettings.Current.Theme = "system";
        string? Bg() => (App.Brush("Bg") as Avalonia.Media.ISolidColorBrush)?.Color.ToString();
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("light", AppSettings.Current.Theme);
        Assert.Equal("#fffff8f0", Bg());   // papiro
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("dark", AppSettings.Current.Theme);
        Assert.Equal("#ff0a1424", Bg());   // noite do Nilo
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("system", AppSettings.Current.Theme);
        w.Close();
    }
}
