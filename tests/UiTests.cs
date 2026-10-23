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
        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
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
        Assert.True(nav.Count >= 2);
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
