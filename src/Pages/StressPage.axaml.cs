using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PharosGaming;

/// <summary>Página Stress test: CPU, RAM e GPU (estilo FurMark) no máximo, com corte automático por temperatura.</summary>
public partial class StressPage : UserControl
{
    private const int HistoryLength = 600; // 5 min a 2 amostras/s
    private readonly StressTester _stress = new();
    private readonly GpuBurn _burn = new();
    private readonly List<double> _cpuHist = new(), _gpuHist = new();
    private readonly AvaloniaList<string> _log = new();
    private readonly WriteableBitmap _bmp = PreviewBitmap.New(GlPreview.W, GlPreview.H);
    private bool _gpuOn, _confirmNoSensor;
    private DateTime _start;
    private TimeSpan _duration;
    private double _cpuLimit = 90, _gpuLimit = 85;
    private float _cpuMax, _gpuMax;
    private long _shown;

    public StressPage()
    {
        InitializeComponent();
        Log.ItemsSource = _log;
        Preview.Source = _bmp;
        Chart.SizeChanged += (_, _) => DrawChart();
        Live.Tick += Tick;
    }

    private bool Running => _stress.Running || _gpuOn;
    private static string? Tag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private void Options_Changed(object? sender, RoutedEventArgs e)
    {
        RamPanel.IsEnabled = ChkRam.IsChecked == true;
        GpuOptions.IsEnabled = GpuLimit.IsEnabled = ChkGpu.IsChecked == true;
        DrawChart();
    }

    private void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (Running) Stop("Parado pelo usuário");
        else Start();
    }

    private void Start()
    {
        Hint.Text = "";
        bool cpu = ChkCpu.IsChecked == true, ram = ChkRam.IsChecked == true, gpu = ChkGpu.IsChecked == true;
        if (!cpu && !ram && !gpu) { Hint.Text = "Ative CPU, RAM ou GPU."; return; }
        if (BenchLog.Busy != null) { Hint.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        _cpuLimit = (double)(CpuLimit.Value ?? 90);
        _gpuLimit = (double)(GpuLimit.Value ?? 85);
        // Sem sensor não há corte automático: pede um segundo clique para seguir assim mesmo.
        var missing = new List<string>();
        if ((cpu || ram) && Live.Cpu?.Temp == null) missing.Add("CPU (carregue o módulo k10temp/zenpower ou coretemp)");
        if (gpu && Live.Gpu?.Temp == null) missing.Add("GPU");
        if (missing.Count > 0 && !_confirmNoSensor)
        {
            _confirmNoSensor = true;
            Hint.Text = $"Sem sensor de temperatura da {string.Join(" e da ", missing)}: o corte automático NÃO vai funcionar. Clique em INICIAR de novo para continuar mesmo assim.";
            return;
        }
        _confirmNoSensor = false;
        double fraction = double.Parse(Tag(RamPanel) ?? "0.6", CultureInfo.InvariantCulture);
        _duration = TimeSpan.FromMinutes((double)(Minutes.Value ?? 0));
        _start = DateTime.Now;
        _cpuMax = _gpuMax = 0;
        _cpuHist.Clear(); _gpuHist.Clear();
        if (cpu || ram) _stress.Start(cpu, ram, fraction);
        string gpuDesc = "não";
        if (gpu)
        {
            var size = (Tag(ResPanel) ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
            int msaa = int.Parse(Tag(MsaaPanel) ?? "4");
            var intensity = (Tag(IntensityPanel) ?? "128,48").Split(',').Select(int.Parse).ToArray();
            _burn.Start(new GpuBurnSettings { Width = size[0], Height = size[1], Msaa = msaa, Shells = intensity[0], Iterations = intensity[1], AdapterName = Live.Gpu?.Name ?? "" });
            _gpuOn = true;
            Preview.IsVisible = true;
            gpuDesc = $"sim ({size[0]}x{size[1]}, {(msaa > 1 ? $"MSAA {msaa}x" : "sem MSAA")}, " +
                      $"{IntensityPanel.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Content}, limite {_gpuLimit:0} °C)";
        }
        BenchLog.Busy = "Stress test";
        SetControls(true);
        SetStatus("Rodando", alert: false);
        AddLog($"Iniciado · CPU {(cpu ? $"sim (limite {_cpuLimit:0} °C)" : "não")} · RAM {(ram ? $"sim ({fraction:P0} da livre)" : "não")} · " +
               $"GPU {gpuDesc} · {(_duration > TimeSpan.Zero ? $"{_duration.TotalMinutes:0.##} min" : "sem limite de tempo")}");
    }

    private void Stop(string reason)
    {
        if (!Running) return;
        long errors = _stress.MemoryErrors, passes = _stress.MemoryPasses;
        bool hadRam = _stress.MemoryAllocatedBytes > 0, hadCpu = _stress.Running;
        _stress.Stop();
        string gpuSummary = "";
        if (_gpuOn)
        {
            _burn.Stop();
            _gpuOn = false;
            // "Pontuação" no estilo FurMark: frames renderizados e FPS médio.
            gpuSummary = $" · GPU: {_burn.Frames:N0} frames, média {_burn.AverageFps:0} FPS, temp. máx. {_gpuMax:0.#} °C";
        }
        BenchLog.Busy = null;
        SetControls(false);
        bool aborted = reason.StartsWith("ABORTADO");
        SetStatus(aborted ? "Abortado" : "Parado", aborted);
        AddLog($"{reason} · duração {DateTime.Now - _start:hh\\:mm\\:ss}" +
               (hadCpu ? $" · CPU temp. máx. {_cpuMax:0.#} °C" : "") +
               (hadRam ? $" · RAM {passes} passes, {errors} erros" : "") + gpuSummary);
    }

    private void SetControls(bool running)
    {
        BtnRun.Content = running ? "PARAR" : "INICIAR";
        ChkCpu.IsEnabled = ChkRam.IsEnabled = ChkGpu.IsEnabled = CpuLimit.IsEnabled = Minutes.IsEnabled = !running;
        RamPanel.IsEnabled = !running && ChkRam.IsChecked == true;
        GpuOptions.IsEnabled = GpuLimit.IsEnabled = !running && ChkGpu.IsChecked == true;
    }

    /// <summary>Alerta = cartão invertido (claro com texto escuro).</summary>
    private void SetStatus(string text, bool alert)
    {
        StatusText.Text = text;
        StatusCard.Background = alert ? App.Brush("BadFill") : App.Brush("Card");
        StatusText.Foreground = alert ? App.Brush("OnFlame") : App.Brush("Fg");
        StatusLabel.Foreground = alert ? App.Brush("OnFlame") : App.Brush("Muted");
    }

    private void AddLog(string msg)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {msg}");
        Log.ScrollIntoView(_log.Count - 1);
    }

    private void Tick()
    {
        var c = Live.Cpu; var g = Live.Gpu;
        float? temp = c?.Temp, gTemp = g?.Temp;
        if (temp is float t)
        {
            _cpuHist.Add(t); if (_cpuHist.Count > HistoryLength) _cpuHist.RemoveAt(0);
            if (_stress.Running && t > _cpuMax) _cpuMax = t;
        }
        if (gTemp is float gt)
        {
            _gpuHist.Add(gt); if (_gpuHist.Count > HistoryLength) _gpuHist.RemoveAt(0);
            if (_gpuOn && gt > _gpuMax) _gpuMax = gt;
        }
        // Os cortes de segurança rodam mesmo com a página fora da tela.
        if (Running)
        {
            var elapsed = DateTime.Now - _start;
            if (_stress.Running && temp >= _cpuLimit) Stop($"ABORTADO: CPU a {temp:0.#} °C atingiu o limite de {_cpuLimit:0} °C");
            else if (_gpuOn && gTemp >= _gpuLimit) Stop($"ABORTADO: GPU a {gTemp:0.#} °C atingiu o limite de {_gpuLimit:0} °C");
            else if (_stress.MemoryErrors > 0) Stop($"ABORTADO: {_stress.MemoryErrors} erros de memória detectados");
            else if (_gpuOn && !_burn.Running) Stop(_burn.Error != null ? $"ABORTADO: falha na GPU ({_burn.Error})" : "Render da GPU encerrado");
            else if (_duration > TimeSpan.Zero && elapsed >= _duration) Stop("Concluído (tempo esgotado)");
            Elapsed.Text = elapsed.ToString(@"hh\:mm\:ss");
        }
        if (!IsVisible) return;
        CpuTemp.Text = temp == null ? "—" : $"{temp:0.#} °C";
        CpuTempMax.Text = _cpuMax > 0 ? $"máx. {_cpuMax:0.#} °C" : "máx. —";
        CpuLoad.Text = c?.Total is float l ? $"{l:0} %" : "—";
        CpuClock.Text = c?.ClockMhz is float f ? $"{f:0} MHz" : "—";
        GpuTemp.Text = gTemp == null ? "—" : $"{gTemp:0.#} °C";
        GpuTempMax.Text = _gpuMax > 0 ? $"máx. {_gpuMax:0.#} °C" : "máx. —";
        GpuPower.Text = g?.PowerW is float p ? $"{p:0.#} W" : "—";
        GpuCap.Text = g?.ThermalCap == true ? "limitador: térmico" : g?.Load is float gl ? $"carga {gl:0} %" : "—";
        Fps.Text = _gpuOn && _burn.Fps > 0 ? $"{_burn.Fps:0}" : "—";
        FpsAvg.Text = _gpuOn && _burn.AverageFps > 0 ? $"média {_burn.AverageFps:0}" : "média —";
        Ram.Text = _stress.MemoryAllocatedBytes > 0 ? $"{_stress.MemoryPasses} / {_stress.MemoryErrors}" : "—";
        if (_burn.Preview is { } pv && pv.Version != _shown) { _shown = pv.Version; PreviewBitmap.FromGl(pv, _bmp); Preview.InvalidateVisual(); }
        DrawChart();
    }

    /// <summary>Histórico de temperatura da CPU (claro) e da GPU (cinza) com as linhas de limite.</summary>
    private void DrawChart()
    {
        Chart.Children.Clear();
        double w = Chart.Bounds.Width, h = Chart.Bounds.Height;
        if (w < 50 || h < 40) return;
        const double left = 36, top = 6, bottom = 6;
        double plotW = w - left, plotH = h - top - bottom;
        double yMin = 20, yMax = Math.Max(110, Math.Max(_cpuLimit, _gpuLimit) + 10);
        double Y(double v) => top + plotH * (1 - (Math.Clamp(v, yMin, yMax) - yMin) / (yMax - yMin));
        for (double v = yMin; v <= yMax; v += 20)
        {
            Chart.Children.Add(new Line { StartPoint = new(left, Y(v)), EndPoint = new(w, Y(v)), Stroke = App.Brush("Line") });
            var lbl = new TextBlock { Text = $"{v:0}°", FontSize = 10, Foreground = App.Brush("Faint"), FontFamily = new FontFamily("monospace") };
            Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, Y(v) - 7);
            Chart.Children.Add(lbl);
        }
        void Limit(double v, IBrush b) => Chart.Children.Add(new Line
        {
            StartPoint = new(left, Y(v)), EndPoint = new(w, Y(v)), Stroke = b, StrokeThickness = 1, StrokeDashArray = new AvaloniaList<double> { 4, 3 },
        });
        Limit(_cpuLimit, App.Brush("Bad"));
        if (ChkGpu.IsChecked == true) Limit(_gpuLimit, App.Brush("Bad"));
        double step = plotW / (HistoryLength - 1);
        void Series(List<double> data, IBrush stroke)
        {
            if (data.Count < 2) return;
            int offset = HistoryLength - data.Count; // alinha à direita
            Chart.Children.Add(new Polyline
            {
                Points = data.Select((v, i) => new Point(left + (offset + i) * step, Y(v))).ToList(), Stroke = stroke, StrokeThickness = 1.6,
            });
        }
        Series(_gpuHist, App.Brush("Glow"));
        Series(_cpuHist, App.Brush("Flame"));
    }
}
