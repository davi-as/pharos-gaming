using Avalonia;
using Avalonia.Controls;

namespace PharosGaming;

/// <summary>Janela principal: menu lateral com as páginas agrupadas como no PHAROS GAMING para Windows.</summary>
public partial class MainWindow : Window
{
    /// <summary>Icon = chave em Icons.axaml (ic-...).</summary>
    private sealed record PageDef(string Group, string Title, string Icon, Func<Control> Make)
    {
        public string Subtitle => Subtitles[Title];
    }


    private static readonly PageDef[] All =
    {
        new("Painel", "Visão geral", "view-dashboard-outline", () => new DashPage()),
        new("Painel", "Sistema", "desktop-tower-monitor", () => new SystemPage()),
        new("Painel", "Sensores", "thermometer", () => new SensorsPage()),
        new("Painel", "Placa de vídeo", "expansion-card-variant", () => new GpuPage()),
        new("Painel", "Discos", "harddisk", () => new DisksPage()),
        new("Painel", "Energia", "lightning-bolt-outline", () => new EnergyPage()),
        new("Painel", "Rede", "lan", () => new NetPage()),
        new("Jogos", "Biblioteca", "bookshelf", () => new LibraryPage()),
        new("Jogos", "FPS estimado", "speedometer", () => new FpsPage()),
        new("Jogos", "Gargalo", "scale-balance", () => new BottleneckPage()),
        new("Jogos", "Overlay", "layers-outline", () => new OverlayPage()),
        new("Jogos", "Limitador de FPS", "timer-outline", () => new LimiterPage()),
        new("Console", "PC como console", "gamepad-variant-outline", () => new ConsolePage()),
        new("Desempenho", "Benchmark", "chart-bar", () => new BenchPage()),
        new("Desempenho", "Stress test", "fire", () => new StressPage()),
        new("Desempenho", "Overclock", "rocket-launch-outline", () => new OcPage()),
        new("Ferramentas", "Otimização", "tune-variant", () => new OptimizePage()),
        new("Ferramentas", "Inicialização", "power", () => new StartupPage()),
        new("Ferramentas", "Instalar apps", "package-variant-closed", () => new AppsPage()),
        new("Ferramentas", "Drivers", "chip", () => new DriverPage()),
        new("Ferramentas", "Integração", "palette-outline", () => new IntegPage()),
        new("Maker", "Portas seriais", "serial-port", () => new MakerPortsPage()),
        new("Maker", "Drivers seriais", "usb", () => new MakerDrvPage()),
    };

    /// <summary>Uma frase por página, no cabeçalho (no lugar do parágrafo solto no topo de cada página).</summary>
    private static readonly Dictionary<string, string> Subtitles = new()
    {
        ["Visão geral"] = "FPS, carga e gargalo ao vivo enquanto você joga",
        ["Sistema"] = "Processador, memória, placa-mãe, telas e discos",
        ["Sensores"] = "Todas as leituras de temperatura, clock, consumo e ventoinhas",
        ["Placa de vídeo"] = "Modelo, driver, clocks e limites da GPU",
        ["Discos"] = "Saúde, desgaste e temperatura de cada disco",
        ["Energia"] = "Bateria, fonte de energia e consumo",
        ["Rede"] = "Latência, velocidade e estabilidade da conexão",
        ["Biblioteca"] = "Seus jogos, com opções de inicialização e programas auxiliares",
        ["FPS estimado"] = "Quanto FPS esperar em jogos populares no seu PC",
        ["Gargalo"] = "O que está limitando o seu FPS: GPU, CPU, memória ou temperatura",
        ["Overlay"] = "FPS, frametime e sensores por cima do jogo (MangoHud)",
        ["Limitador de FPS"] = "FPS travado e quadros em ritmo constante, por jogo",
        ["PC como console"] = "Controle, Hydra, Steam, TV e MangoHud trabalhando juntos",
        ["Benchmark"] = "CPU, GPU, ray tracing, memória e disco",
        ["Stress test"] = "Carga máxima com corte automático por temperatura",
        ["Overclock"] = "Ajustes da GPU, teste de estabilidade e guia da BIOS",
        ["Otimização"] = "Libera memória, fecha o que atrapalha e ajusta o sistema para jogos",
        ["Inicialização"] = "Programas que abrem sozinhos com a sessão",
        ["Instalar apps"] = "Programas essenciais dos repositórios oficiais",
        ["Drivers"] = "Driver de vídeo, atualizações do sistema e firmware",
        ["Integração"] = "Iluminação RGB pelo OpenRGB, com efeitos que reagem ao jogo",
        ["Portas seriais"] = "Arduino, ESP32 e conversores: acesso, conflitos e nomes fixos",
        ["Drivers seriais"] = "CH340, CP210x, FTDI e Prolific: disponíveis e carregados",
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
                Nav.Children.Add(new TextBlock { Text = group.ToUpperInvariant(), Classes = { "label", "navgroup" }, Margin = new(12, Nav.Children.Count == 0 ? 4 : 16, 0, 4) });
            }
            var b = new RadioButton
            {
                GroupName = "nav", Classes = { "nav" }, Tag = p.Title,
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 11,
                    Children = { Icon(p.Icon, 17), new TextBlock { Text = p.Title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
                },
            };
            b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) Show(p); };
            Nav.Children.Add(b);
        }
        ShowTheme();
        Live.Start();
        Live.Tick += () =>
        {
            var f = Live.Frames;
            Status.Text = f != null ? $"{f.Game} · {f.Fps:N0} FPS" : "Nenhum jogo com MangoHud";
            StatusDot.Fill = f != null ? App.Brush("Ok") : App.Brush("Faint");
        };
        Nav.Children.OfType<RadioButton>().First().IsChecked = true;
    }

    // Sistema → Claro → Escuro: as cores são dinâmicas, a troca vale na hora
    private void Theme_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.Theme = s.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        try { s.Save(); } catch { }
        App.ApplyTheme(s.Theme);
        ShowTheme();
    }

    private void ShowTheme()
    {
        var (icon, text) = AppSettings.Current.Theme switch
        {
            "light" => ("white-balance-sunny", "Claro"), "dark" => ("weather-night", "Escuro"), _ => ("theme-light-dark", "Sistema"),
        };
        BtnTheme.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
            Children = { Icon(icon, 14), new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
        };
        ToolTip.SetTip(BtnTheme, "Tema: clique para alternar entre sistema, claro e escuro");
    }

    /// <summary>Ícone do Icons.axaml; a cor segue o Foreground do item (menu ativo = lápis-lazúli).</summary>
    public static PathIcon Icon(string name, double size) => new()
    {
        Data = (Avalonia.Media.Geometry)Application.Current!.FindResource("ic-" + name)!, Width = size, Height = size,
    };

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
        PageSub.Text = p.Subtitle;
        PageIcon.Data = (Avalonia.Media.Geometry)Application.Current!.FindResource("ic-" + p.Icon)!;
    }
}
