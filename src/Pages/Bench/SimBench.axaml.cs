using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Simulação de jogo (CPU): quanto FPS a CPU aguenta com milhares de unidades.</summary>
public partial class SimBench : UserControl
{
    private const int View = 440;
    private readonly WriteableBitmap _bmp = PreviewBitmap.New(View, View);
    private readonly int[] _px = new int[View * View];
    private CancellationTokenSource? _cts;

    public SimBench()
    {
        InitializeComponent();
        Img.Source = _bmp;
        History.ItemsSource = BenchLog.History(t => t == "sim");
    }

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (BenchLog.Busy != null) { Status.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        int secs = int.Parse(DurPanel.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag as string ?? "20");
        var sim = new GameSimBench();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BenchLog.Busy = "Simulação de jogo (CPU)";
        BtnRun.Content = "Cancelar";
        DurPanel.IsEnabled = false;
        Result.ItemsSource = null;
        Idle.IsVisible = false;
        Status.Text = "Aquecendo (2 s)…";
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) =>
        {
            Draw(sim);
            Progress.Value = sim.Progress;
            if (sim.FramesDone > 0) Status.Text = $"Simulando · {sim.Progress:P0}";
            FpsNow.Text = sim.CurrentFps > 0 ? $"{sim.CurrentFps:0} FPS" : "";
        });
        timer.Start();
        var r = await Task.Run(() => sim.Run(TimeSpan.FromSeconds(secs), ct));
        timer.Stop();
        bool cancelled = ct.IsCancellationRequested;
        _cts = null;
        BenchLog.Busy = null;
        BtnRun.Content = "Iniciar";
        DurPanel.IsEnabled = true;
        Progress.Value = 0;
        FpsNow.Text = "";
        if (cancelled) { Status.Text = "Cancelado"; return; }
        Status.Text = $"Concluído · {r.Frames:N0} frames em {r.Seconds:0} s";
        Result.ItemsSource = new[]
        {
            new Kv("FPS médio (nota)", $"{r.AvgFps:0}"),
            new Kv("1% low", $"{r.Low1Fps:0}"),
            new Kv("Thread principal", $"{r.MainThreadMs:0.00} ms/frame"),
            new Kv("Física + IA (paralelo)", $"{r.ParallelMs:0.00} ms/frame"),
            new Kv("Unidades", $"{GameSimBench.Entities:N0}"),
        };
        BenchLog.Save("sim", r.Score, $"{r.AvgFps:0} FPS · 1% low {r.Low1Fps:0}");
        History.ItemsSource = BenchLog.History(t => t == "sim");
    }

    /// <summary>Vista de cima da batalha: time A em branco, time B em cinza.</summary>
    private void Draw(GameSimBench sim)
    {
        Array.Fill(_px, unchecked((int)0xFF050505));
        float[] xs = sim.ViewX, ys = sim.ViewY; byte[] team = sim.ViewTeam;
        float k = View / GameSimBench.World;
        for (int i = 0; i < GameSimBench.Entities; i++)
        {
            int px = (int)(xs[i] * k), py = (int)(ys[i] * k);
            if ((uint)px < View && (uint)py < View)
                _px[py * View + px] = team[i] == 0 ? unchecked((int)0xFFF5F5F5) : unchecked((int)0xFF5A5A5A);
        }
        PreviewBitmap.FromBgra(_px, View, View, _bmp);
        Img.InvalidateVisual();
    }
}
