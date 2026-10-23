using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Render (CPU): path tracing por blocos em todas as threads, como o Cinebench.</summary>
public partial class RenderBench : UserControl
{
    private readonly BenchRenderer _bench = new();
    private readonly WriteableBitmap _bmp = PreviewBitmap.New(BenchRenderer.Width, BenchRenderer.Height);
    private CancellationTokenSource? _cts;
    private DateTime _start, _phaseStart;
    private string _phase = "";
    private readonly DispatcherTimer _view;

    public RenderBench()
    {
        InitializeComponent();
        Img.Source = _bmp;
        _view = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => UpdateView());
        Live.Tick += UpdateLive;
        UpdateHistory();
    }

    private static string? CheckedTag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (BenchLog.Busy != null) { Hint.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        string mode = CheckedTag(ModePanel) ?? "both";
        bool sustained = CheckedTag(DurationPanel) == "10";
        var minDuration = sustained ? TimeSpan.FromMinutes(10) : TimeSpan.Zero;
        int threads = Environment.ProcessorCount;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BenchLog.Busy = "Render (CPU)";
        BtnRun.Content = "PARAR";
        ModePanel.IsEnabled = DurationPanel.IsEnabled = false;
        Hint.Text = "";
        Idle.IsVisible = false;
        Mt.Text = St.Text = "—";
        Ratio.Text = "";
        _start = DateTime.Now;
        _view.Start();
        double? mt = null, st = null;
        try
        {
            if (mode is "mt" or "both")
            {
                _phase = $"Multi-core · {threads} threads"; _phaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(threads, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { mt = r.Score; Mt.Text = $"{r.Score:N0}"; }
            }
            if (!ct.IsCancellationRequested && mode is "st" or "both")
            {
                _phase = "Single-core · 1 thread"; _phaseStart = DateTime.Now;
                var r = await Task.Run(() => _bench.Run(1, minDuration, ct), ct);
                if (!ct.IsCancellationRequested) { st = r.Score; St.Text = $"{r.Score:N0}"; }
            }
        }
        catch (OperationCanceledException) { }
        _view.Stop();
        UpdateView();
        bool cancelled = ct.IsCancellationRequested;
        _cts = null;
        BenchLog.Busy = null;
        BtnRun.Content = "INICIAR";
        ModePanel.IsEnabled = DurationPanel.IsEnabled = true;
        Buckets.Children.Clear();
        if (cancelled) { Status.Text = "Cancelado"; return; }
        Status.Text = $"Concluído em {DateTime.Now - _start:m\\:ss}";
        if (mt is double m && st is double s)
            Ratio.Text = $"Multi ÷ single: {m / s:0.0}x com {threads} threads ({threads / 2} núcleos com SMT costumam render 8–12x).";
        var set = AppSettings.Current;
        set.BenchRuns.Insert(0, new BenchRun(DateTime.Now, mt, st, sustained, GameCatalog.ShortCpuName(Live.Cpu?.Model ?? "")));
        if (set.BenchRuns.Count > 10) set.BenchRuns.RemoveRange(10, set.BenchRuns.Count - 10);
        try { set.Save(); } catch { }
        UpdateHistory();
    }

    /// <summary>Copia a imagem parcial e desenha o contorno dos blocos que cada thread está renderizando.</summary>
    private void UpdateView()
    {
        PreviewBitmap.FromBgra(_bench.Pixels, BenchRenderer.Width, BenchRenderer.Height, _bmp);
        Img.InvalidateVisual();
        Buckets.Children.Clear();
        foreach (int b in _bench.Active)
        {
            if (b < 0) continue;
            var (x, y, w, h) = _bench.Buckets[b];
            var rect = new Rectangle { Width = w, Height = h, Stroke = Brushes.White, StrokeThickness = 1.5 };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            Buckets.Children.Add(rect);
        }
        if (_cts == null) { Progress.Value = 0; return; }
        double frac = _bench.BucketsTotal > 0 ? (double)_bench.BucketsDone / _bench.BucketsTotal : 0;
        Progress.Value = frac;
        // Tempo restante da passada atual, pela velocidade até agora (só depois de 3% para não oscilar).
        var el = DateTime.Now - _phaseStart;
        string eta = frac >= 0.03 ? $" · faltam ~{TimeSpan.FromSeconds(el.TotalSeconds * (1 - frac) / frac):m\\:ss}" : "";
        Status.Text = $"{_phase} · {frac:P0}{eta}";
        Elapsed.Text = (DateTime.Now - _start).ToString(@"mm\:ss");
    }

    private void UpdateLive()
    {
        if (!IsVisible || Live.Cpu is not { } c) return;
        var power = SensorMonitor.Shared.Rows.FirstOrDefault(r => r.Type == SensorType.Power && r.Hardware == c.Model);
        LiveRows.ItemsSource = new[]
        {
            new Kv("CPU", GameCatalog.ShortCpuName(c.Model)),
            new Kv("Threads", $"{c.Threads}"),
            new Kv("Temperatura", c.Temp is float t ? $"{t:0.#} °C" : "—"),
            new Kv("Clock médio", c.ClockMhz is float f ? $"{f:0} MHz" : "—"),
            new Kv("Consumo", power?.Value ?? "—"),
            new Kv("Carga", c.Total is float l ? $"{l:0} %" : "—"),
        };
    }

    private void UpdateHistory()
    {
        var runs = AppSettings.Current.BenchRuns;
        HistoryEmpty.IsVisible = runs.Count == 0;
        double bestMt = runs.Max(r => r.Multi) ?? 1, bestSt = runs.Max(r => r.Single) ?? 1;
        History.ItemsSource = runs.Select(r => new MeterItem(
            $"{r.Date:dd/MM HH:mm}{(r.Sustained ? " · 10 min" : "")}",
            $"{(r.Multi is double m ? $"{m:N0}" : "—")} / {(r.Single is double s ? $"{s:N0}" : "—")}",
            r.Multi is double mm ? mm / bestMt : r.Single is double ss ? ss / bestSt : 0)).ToList();
    }
}
