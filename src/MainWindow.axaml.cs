using Avalonia.Controls;

namespace PharosGaming;

/// <summary>Janela principal: menu lateral com as páginas agrupadas como no PHAROS GAMING para Windows.</summary>
public partial class MainWindow : Window
{
    private sealed record PageDef(string Group, string Title, Func<Control> Make);

    private static readonly PageDef[] All =
    {
        new("PHAROS GAMING", "Visão geral", () => new DashPage()),
        new("PHAROS GAMING", "Sistema", () => new SystemPage()),
        new("PHAROS GAMING", "Sensores", () => new SensorsPage()),
        new("PHAROS GAMING", "Placa de vídeo", () => new GpuPage()),
        new("PHAROS GAMING", "Discos", () => new DisksPage()),
        new("PHAROS GAMING", "Energia", () => new EnergyPage()),
        new("PHAROS GAMING", "Rede", () => new NetPage()),
        new("PHAROS JOGOS", "Biblioteca", () => new LibraryPage()),
        new("PHAROS JOGOS", "FPS estimado", () => new FpsPage()),
        new("PHAROS JOGOS", "Gargalo", () => new BottleneckPage()),
        new("PHAROS JOGOS", "Overlay", () => new OverlayPage()),
        new("PHAROS DESEMPENHO", "Benchmark", () => new BenchPage()),
        new("PHAROS DESEMPENHO", "Stress test", () => new StressPage()),
        new("PHAROS DESEMPENHO", "Overclock", () => new OcPage()),
    };

    private readonly Dictionary<PageDef, Control> _made = new();

    public MainWindow()
    {
        InitializeComponent();
        Version.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "");
        string? group = null;
        foreach (var p in All)
        {
            if (p.Group != group)
            {
                group = p.Group;
                Nav.Children.Add(new TextBlock { Text = group, Classes = { "label" }, Margin = new(10, Nav.Children.Count == 0 ? 4 : 18, 0, 6) });
            }
            var b = new RadioButton { Content = p.Title, GroupName = "nav", Classes = { "nav" } };
            b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) Show(p); };
            Nav.Children.Add(b);
        }
        Live.Start();
        Live.Tick += () => Status.Text = Live.Frames is { } f ? $"● {f.Game} · {f.Fps:N0} FPS" : "nenhum jogo com MangoHud";
        Nav.Children.OfType<RadioButton>().First().IsChecked = true;
    }

    private void Show(PageDef p)
    {
        // Páginas são criadas na primeira visita: cada uma só lê o hardware quando aberta.
        if (!_made.TryGetValue(p, out var c))
        {
            c = p.Make();
            _made[p] = c;
            Pages.Children.Add(c);
        }
        foreach (var child in Pages.Children) child.IsVisible = child == c;
        PageTitle.Text = p.Title;
    }
}
