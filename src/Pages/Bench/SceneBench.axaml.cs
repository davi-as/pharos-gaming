using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Cena de jogo (GPU): vale com terreno, 40 mil árvores, sombras e água, no estilo 3DMark / Unigine.</summary>
public partial class SceneBench : UserControl
{
    private GpuGameBench? _bench;
    private readonly WriteableBitmap _bmp = PreviewBitmap.New(GlPreview.W, GlPreview.H);
    private long _shown;

    public SceneBench()
    {
        InitializeComponent();
        Img.Source = _bmp;
        History.ItemsSource = BenchLog.History(t => t == "gpu");
    }

    private static string? Tag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_bench != null) { _bench.Cancel(); return; }
        if (BenchLog.Busy != null) { Status.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        var size = (Tag(ResPanel) ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
        int secs = int.Parse(Tag(DurPanel) ?? "60");
        var gpus = await Task.Run(GpuInfoCollector.Collect);
        var bench = _bench = new GpuGameBench { Width = size[0], Height = size[1], Duration = TimeSpan.FromSeconds(secs), AdapterName = gpus.FirstOrDefault()?.Name ?? "" };
        BenchLog.Busy = "Cena de jogo (GPU)";
        BtnRun.Content = "CANCELAR";
        ResPanel.IsEnabled = DurPanel.IsEnabled = false;
        Result.ItemsSource = null;
        Idle.IsVisible = false;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            FpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            Progress.Value = bench.Progress;
            Status.Text = $"Rodando · {size[0]}x{size[1]} · {bench.Progress:P0}";
            if (bench.Preview is { } p && p.Version != _shown) { _shown = p.Version; PreviewBitmap.FromGl(p, _bmp); Img.InvalidateVisual(); }
        });
        timer.Start();
        Live.Paused = true;
        // Thread dedicada: a cena roda num loop próprio.
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        Live.Paused = false;
        BenchLog.Busy = null;
        _bench = null;
        BtnRun.Content = "INICIAR";
        ResPanel.IsEnabled = DurPanel.IsEnabled = true;
        Progress.Value = 0;
        FpsNow.Text = "—";
        if (r == null) { Status.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}"; return; }
        Status.Text = $"Concluído · {r.Adapter}";
        Result.ItemsSource = new[]
        {
            new Kv("Nota", $"{r.Score:N0} pts"),
            new Kv("FPS médio", $"{r.AvgFps:0.0}"),
            new Kv("1% low", $"{r.Low1Fps:0.0}"),
            new Kv("Resolução", $"{r.Width}x{r.Height}"),
            new Kv("Frames", $"{r.Frames:N0}"),
        };
        BenchLog.Save("gpu", r.Score, $"{r.Score:N0} pts · {r.AvgFps:0} FPS · {r.Height}p");
        History.ItemsSource = BenchLog.History(t => t == "gpu");
    }
}
