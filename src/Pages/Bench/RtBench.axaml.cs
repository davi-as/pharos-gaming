using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Ray tracing (GPU): aquecimento + 3 passadas, nota pela mediana e variação entre passadas.</summary>
public partial class RtBench : UserControl
{
    private GpuRtBench? _bench;
    private readonly WriteableBitmap _bmp = PreviewBitmap.New(GlPreview.W, GlPreview.H);
    private long _shown;

    public RtBench()
    {
        InitializeComponent();
        Img.Source = _bmp;
        History.ItemsSource = BenchLog.History(t => t.StartsWith("rt:"));
    }

    private static string? CheckedTag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
    private static string PresetName(string p) => p switch { "normal" => "Normal", "extrema" => "Extrema", _ => "Alta" };

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_bench != null) { _bench.Cancel(); return; }
        if (BenchLog.Busy != null) { Status.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        var size = (CheckedTag(ResPanel) ?? "2560x1440").Split('x').Select(int.Parse).ToArray();
        string preset = CheckedTag(PresetPanel) ?? "alta";
        bool quick = CheckedTag(ModePanel) == "quick";
        var gpus = await Task.Run(GpuInfoCollector.Collect);
        var bench = _bench = new GpuRtBench
        {
            Width = size[0], Height = size[1], Preset = preset, AdapterName = gpus.FirstOrDefault()?.Name ?? "", Passes = 3,
            Warmup = TimeSpan.FromSeconds(quick ? 5 : 20), PassDuration = TimeSpan.FromSeconds(quick ? 10 : 30),
        };
        BenchLog.Busy = "Ray tracing (GPU)";
        BtnRun.Content = "Cancelar";
        ResPanel.IsEnabled = PresetPanel.IsEnabled = ModePanel.IsEnabled = false;
        Result.ItemsSource = null;
        Verdict.Text = "";
        Idle.IsVisible = false;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            FpsNow.Text = bench.CurrentFps > 0 ? $"{bench.CurrentFps:0}" : "—";
            Phase.Text = bench.Phase;
            Progress.Value = bench.Progress;
            Status.Text = $"Rodando · {size[0]}x{size[1]} · {PresetName(preset)} · {bench.Progress:P0}";
            if (bench.Preview is { } p && p.Version != _shown) { _shown = p.Version; PreviewBitmap.FromGl(p, _bmp); Img.InvalidateVisual(); }
        });
        timer.Start();
        Live.Paused = true; // consultas ao driver geram engasgos no frametime
        var r = await Task.Factory.StartNew(bench.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Stop();
        Live.Paused = false;
        BenchLog.Busy = null;
        _bench = null;
        BtnRun.Content = "Iniciar";
        ResPanel.IsEnabled = PresetPanel.IsEnabled = ModePanel.IsEnabled = true;
        Progress.Value = 0;
        FpsNow.Text = "—";
        Phase.Text = "";
        if (r == null) { Status.Text = bench.Cancelled ? "Cancelado" : $"Falhou: {bench.Error}"; return; }
        Status.Text = $"Concluído · {r.Adapter}" + (Nvml.DriverVersion() is string dv ? $" · driver {dv}" : "");
        Verdict.Text = r.Reliable
            ? $"✔ Resultado confiável: as 3 passadas variaram só {r.VariationPct:0.0}%."
            : $"⚠ Variação de {r.VariationPct:0.0}% entre as passadas (acima de 3%). Algo atrapalhou: programa em segundo plano, " +
              "temperatura ou limite de energia. Feche outros programas e rode de novo antes de comparar.";
        var rows = new List<Kv>
        {
            new("Nota", $"{r.Score:N0} pts"),
            new("FPS (mediana)", $"{r.MedianFps:0.0}"),
            new("1% low (mediana)", $"{r.MedianLow1:0.0}"),
            new("Variação entre passadas", $"{r.VariationPct:0.0}%"),
            new("Raios por segundo (estimado)", $"{r.GigaRaysPerSec:0.0} bilhões"),
            new("Preset · resolução", $"{PresetName(r.Preset)} · {r.Width}x{r.Height}"),
        };
        for (int i = 0; i < r.Passes.Count; i++) rows.Add(new($"Passada {i + 1}", $"{r.Passes[i].AvgFps:0.0} FPS · 1% low {r.Passes[i].Low1Fps:0.0}"));
        Result.ItemsSource = rows;
        BenchLog.Save($"rt:{r.Preset}:{r.Height}", r.Score,
                      $"{r.Score:N0} pts · {r.MedianFps:0} FPS · {PresetName(r.Preset)} {r.Height}p" + (r.Reliable ? "" : " · instável"));
        History.ItemsSource = BenchLog.History(t => t.StartsWith("rt:"));
    }
}
