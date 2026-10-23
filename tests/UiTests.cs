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
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("pharos-xdg").FullName); // MangoHud.conf, autostart, buttons.env
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
        Assert.Equal(24, nav.Count); // as 21 páginas do PHAROS GAMING para Windows + Limitador de FPS, Sessões e PC como console
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

/// <summary>Capturas de todas as páginas para revisar a interface: PHAROS_SHOTS=pasta dotnet test --filter Screenshots.</summary>
public class Screenshots
{
    [AvaloniaFact]
    public void SaveEveryPage()
    {
        if (Environment.GetEnvironmentVariable("PHAROS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var w = new MainWindow { Width = 1400, Height = 900 };
        w.Show();
        if (Environment.GetEnvironmentVariable("PHAROS_SHOTS_THEME") is { Length: > 0 } theme) App.ApplyTheme(theme); // light | dark
        if (Environment.GetEnvironmentVariable("PHAROS_SHOTS_SESSIONS") is { Length: > 0 } sess) File.Copy(sess, SessionLog.FilePath, true); // histórico de verdade na captura
        var nav = w.FindControl<StackPanel>("Nav")!.Children.OfType<RadioButton>().ToList();
        for (int i = 0; i < nav.Count; i++)
        {
            nav[i].IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Live.Step();
            Live.Step();
            for (int t = 0; t < 30; t++) // leituras assíncronas (systemctl, /proc) voltam pela fila da interface
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"{i:00}-{nav[i].Tag ?? i}.png"));
        }
        w.Close();
        var tv = new TvWindow { WindowState = WindowState.Normal, Width = 1600, Height = 900 };
        tv.Show();
        for (int t = 0; t < 50; t++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Live.Step(); Thread.Sleep(50); }
        tv.CaptureRenderedFrame()?.Save(Path.Combine(dir, "99-modo-tv.png"));
        tv.Close();
    }
}
