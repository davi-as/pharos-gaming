using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>
/// Painel ao vivo para deixar num segundo monitor enquanto joga: FPS (MangoHud), carga e temperatura de GPU/CPU/RAM
/// e o diagnóstico de gargalo (as mesmas regras do PHAROS GAMING para Windows).
/// </summary>
public partial class MainWindow : Window
{
    private const double TickSec = 0.5;
    private const int FpsPoints = 120, LoadPoints = 240; // 60 s e 2 min

    private readonly LinuxSensors _sensors = new();
    private readonly MangoHudFrames _frames = new();
    private readonly List<double?> _fpsHist = new(), _gpuHist = new(), _cpuHist = new();
    private BottleneckSession? _session;
    private string? _sessionGame;

    public MainWindow()
    {
        InitializeComponent();
        LaunchOption.Text = MangoHudFrames.LaunchOption;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSec) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private async void CopyLaunch_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(LaunchOption.Text);
    }

    private void Tick()
    {
        var (cpu, gpu, mem) = _sensors.Sample();
        var frames = _frames.Poll();
        var sample = BottleneckAnalyzer.Sample(cpu, gpu, mem, frames);

        ShowFrames(frames);
        ShowHardware(cpu, gpu, mem);
        ShowBottleneck(sample, frames);

        Push(_fpsHist, frames?.Fps, FpsPoints);
        Push(_gpuHist, gpu?.Load, LoadPoints);
        Push(_cpuHist, cpu.MaxThread, LoadPoints);
        double fpsMax = Math.Max(60, _fpsHist.Max() ?? 0) * 1.1;
        Draw(FpsChart, _fpsHist, FpsPoints, fpsMax, Brush("Fg"));
        LoadChart.Children.Clear();
        Draw(LoadChart, _gpuHist, LoadPoints, 100, Brush("Fg"), clear: false);
        Draw(LoadChart, _cpuHist, LoadPoints, 100, Brush("Muted"), clear: false);
    }

    private void ShowFrames(FrameStats? f)
    {
        Setup.IsVisible = f == null && _fpsHist.All(v => v == null);
        Status.Text = f != null ? $"● {f.Game}" : "nenhum jogo com MangoHud";
        FpsText.Text = f != null ? $"{f.Fps:N0}" : "—";
        FtText.Text = f != null ? $"{f.FrameTimeMs:N1} ms" : "—";
        MinText.Text = f != null ? $"{f.MinFps:N0}" : "—";
        AvgText.Text = _session?.AvgFps is double a ? $"{a:N0}" : "—";
    }

    private void ShowHardware(CpuReading cpu, GpuReading? gpu, MemReading mem)
    {
        GpuName.Text = gpu?.Name ?? "GPU não detectada (NVIDIA via NVML ou AMD via amdgpu)";
        GpuLoad.Text = Pct(gpu?.Load);
        GpuBar.Value = gpu?.Load ?? 0;
        GpuDetail.Text = Join(F(gpu?.Temp, "°C"), F(gpu?.ClockMhz, " MHz"), F(gpu?.PowerW, " W"), F(gpu?.Fan, "% fan"),
                              gpu?.ThermalCap == true ? "LIMITE TÉRMICO" : null);
        VramBar.Value = gpu?.VramPct ?? 0;
        VramText.Text = gpu?.VramUsedMb is double u && gpu.VramTotalMb is double t ? $"{u / 1024:N1} / {t / 1024:N1} GB" : "—";

        CpuName.Text = $"{cpu.Model} · {cpu.Threads} threads";
        CpuLoad.Text = Pct(cpu.Total);
        CpuBar.Value = cpu.Total ?? 0;
        CpuDetail.Text = Join(F(cpu.Temp, "°C"), F(cpu.ClockMhz, " MHz"),
                              cpu.MaxThread is float m ? $"thread mais ocupada {m:N0}%" : null);
        RamBar.Value = mem.Pct;
        RamText.Text = $"{mem.UsedGb:N1} / {mem.TotalGb:N1} GB" + (mem.SwapUsedGb >= 0.1 ? $" · swap {mem.SwapUsedGb:N1} GB" : "");
    }

    private void ShowBottleneck(LoadSample s, FrameStats? f)
    {
        var lim = s.Classify();
        LimTitle.Text = BottleneckAnalyzer.Title(lim);
        LimAdvice.Text = BottleneckAnalyzer.Advice(lim);

        // A sessão acumula enquanto o jogo envia FPS; troca de jogo começa outra.
        if (f != null)
        {
            if (_session == null || _sessionGame != f.Game)
            {
                _session = new BottleneckSession();
                _sessionGame = f.Game;
            }
            _session.Add(s);
        }
        if (_session == null) return;
        var min = (DateTime.Now - _session.Start).TotalMinutes;
        SessionInfo.Text = $"{_sessionGame} · {min:N0} min" + (f == null ? " · encerrada" : "");
        SessionDist.Text = string.Join("\n", _session.Distribution().Select(d => $"{d.Pct,3:N0}%  {BottleneckAnalyzer.Title(d.Limiter)}"));
    }

    // ---------------- gráficos ----------------

    private static void Push(List<double?> list, double? v, int max)
    {
        list.Add(v);
        if (list.Count > max) list.RemoveAt(0);
    }

    /// <summary>Linha com os pontos mais recentes à direita; lacunas (null) quebram a linha.</summary>
    private static void Draw(Canvas c, List<double?> vals, int capacity, double max, IBrush brush, bool clear = true)
    {
        if (clear) c.Children.Clear();
        double w = c.Bounds.Width, h = c.Bounds.Height, step = w / (capacity - 1);
        int offset = capacity - vals.Count;
        var pts = new List<Point>();
        void Flush()
        {
            if (pts.Count > 1) c.Children.Add(new Polyline { Points = pts.ToList(), Stroke = brush, StrokeThickness = 2 });
            pts.Clear();
        }
        for (int i = 0; i < vals.Count; i++)
        {
            if (vals[i] is double v) pts.Add(new Point((offset + i) * step, h - Math.Clamp(v / max, 0, 1) * h));
            else Flush();
        }
        Flush();
    }

    // ---------------- formatação ----------------

    private IBrush Brush(string key) => (IBrush)Application.Current!.Resources[key]!;
    private static string Pct(float? v) => v is float x ? $"{x:N0}%" : "—";
    private static string? F(float? v, string unit) => v is float x ? $"{x:N0}{unit}" : null;
    private static string Join(params string?[] parts) => string.Join("  ·  ", parts.Where(p => p != null));
}
