using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Benchmark (PHAROS BENCH): seis testes, cada um num painel criado na primeira visita.</summary>
public partial class BenchPage : UserControl
{
    private readonly Dictionary<string, Control> _panels = new();

    public BenchPage()
    {
        InitializeComponent();
        Show("render");
    }

    private void Test_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string t } && Host != null) Show(t);
    }

    private void Show(string test)
    {
        if (!_panels.TryGetValue(test, out var c))
        {
            c = test switch
            {
                "gpu" => new SceneBench(), "rt" => new RtBench(), "sim" => new SimBench(),
                "mem" => new IoBench(false), "disk" => new IoBench(true), _ => new RenderBench(),
            };
            _panels[test] = c;
            Host.Children.Add(c);
        }
        foreach (var p in Host.Children) p.IsVisible = p == c;
        Sub.Text = test switch
        {
            "gpu" => "Cena de jogo em OpenGL para medir a placa de vídeo, no estilo 3DMark / Unigine",
            "rt" => "Path tracing com sombras, oclusão e reflexos (compute shader), com 3 passadas e nota pela mediana",
            "sim" => "Quanto FPS a CPU aguenta numa simulação de jogo com milhares de unidades",
            "mem" => "Latência e banda da RAM e velocidade de carregamento do disco",
            "disk" => "Velocidade de leitura e escrita do disco: sequencial e acessos aleatórios de 4 KB, no estilo CrystalDiskMark",
            _ => "Benchmark de CPU por renderização 3D (path tracing), no mesmo princípio do Cinebench",
        };
    }
}
