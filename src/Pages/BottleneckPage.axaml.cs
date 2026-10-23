using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PharosGaming;

/// <summary>Página Gargalo: equilíbrio teórico CPU × GPU, veredito ao vivo e análise de uma sessão de jogo.</summary>
public partial class BottleneckPage : UserControl
{
    private double _ratio = 1;
    private BottleneckSession? _session;

    public BottleneckPage()
    {
        InitializeComponent();
        BalanceCanvas.SizeChanged += (_, _) => PlaceDot();
        Live.Tick += Update;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        string cpu = SystemInfo.ParseCpuInfo(Sys.Read("/proc/cpuinfo") ?? "").Model;
        string gpu = (await Task.Run(GpuInfoCollector.Collect)).FirstOrDefault()?.Name ?? "";
        var (ratio, text) = BottleneckAnalyzer.Balance(GameCatalog.GpuPerf(gpu) ?? 50, GameCatalog.CpuPerf(cpu) ?? 80);
        _ratio = ratio;
        CpuName.Text = $"CPU · {GameCatalog.ShortCpuName(cpu)}";
        GpuName.Text = $"GPU · {GameCatalog.ShortGpuName(gpu)}";
        BalanceText.Text = text;
        PlaceDot();
    }

    /// <summary>Posição do marcador: escala logarítmica da razão GPU/CPU (0,4× … 2,5×), centro = equilibrado.</summary>
    private void PlaceDot()
    {
        double w = BalanceCanvas.Bounds.Width;
        double t = Math.Clamp((Math.Log(_ratio) - Math.Log(0.4)) / (Math.Log(2.5) - Math.Log(0.4)), 0, 1);
        Canvas.SetLeft(BalanceDot, t * Math.Max(0, w - BalanceDot.Width));
    }


    private void Update()
    {
        if (Live.Cpu == null || (!IsVisible && _session == null)) return;
        var frames = Live.Frames;
        var s = Live.Sample();
        var lim = s.Classify();

        LiveTitle.Text = BottleneckAnalyzer.Title(lim);
        LiveAdvice.Text = BottleneckAnalyzer.Advice(lim);
        LiveGame.Text = frames != null
            ? $"{frames.Game} · {frames.Fps:0} FPS · mínimo 10 s {frames.MinFps:0}"
            : "Nenhum jogo enviando FPS pelo MangoHud (as cargas abaixo valem mesmo assim)";
        bool alert = lim is Limiter.Cpu or Limiter.Vram or Limiter.Ram or Limiter.Thermal;
        LiveCard.Background = alert ? App.Brush("BadFill") : App.Brush("Card");
        LiveTitle.Foreground = alert ? App.Brush("OnFlame") : App.Brush("Fg");
        LiveLabel.Foreground = LiveGame.Foreground = LiveAdvice.Foreground = alert ? App.Brush("OnFlame") : App.Brush("Muted");

        Meters.ItemsSource = new[]
        {
            MeterItem.Pct("Carga da GPU", s.GpuLoad),
            MeterItem.Pct("Carga total da CPU", s.CpuTotal),
            MeterItem.Pct(s.BusiestCore != null ? $"Núcleo mais ocupado ({s.BusiestCore})" : "Núcleo mais ocupado", s.CpuMaxCore),
            MeterItem.Pct("VRAM em uso", s.VramPct),
            MeterItem.Pct("RAM em uso", s.RamPct),
            new MeterItem("Temperaturas (GPU / CPU)",
                          $"{(s.GpuTemp is float g ? $"{g:0} °C" : "—")} / {(s.CpuTemp is float c ? $"{c:0} °C" : "—")}",
                          Math.Clamp(Math.Max(s.GpuTemp ?? 0, s.CpuTemp ?? 0) / 100.0, 0, 1)),
        };

        if (_session == null) return;
        _session.Add(s);
        var el = DateTime.Now - _session.Start;
        SessionStatus.Text = $"Coletando… {el:m\\:ss} de {_session.Duration:m\\:ss} · {_session.Count} amostras · " +
                             (frames != null ? $"jogo detectado: {frames.Game}" : "abra o jogo agora");
        if (_session.Done) Finish();
    }

    private void Session_Click(object? sender, RoutedEventArgs e)
    {
        if (_session != null)
        {
            _session = null;
            BtnSession.Content = "Iniciar análise";
            SessionStatus.Text = "Análise cancelada.";
            return;
        }
        int minutes = int.Parse(DurationPanel.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag as string ?? "1");
        _session = new BottleneckSession { Duration = TimeSpan.FromMinutes(minutes) };
        Result.IsVisible = false;
        BtnSession.Content = "Cancelar";
        SessionStatus.Text = "Coletando… abra o jogo agora.";
    }

    private void Finish()
    {
        var sess = _session!;
        _session = null;
        BtnSession.Content = "Iniciar análise";
        SessionStatus.Text = $"Análise concluída às {DateTime.Now:HH:mm} ({sess.Count} amostras).";
        var main = sess.Main ?? Limiter.Capped;
        ResultTitle.Text = $"Resultado: {BottleneckAnalyzer.Title(main)}";
        var parts = new List<string>();
        if (sess.AvgFps is double fps) parts.Add($"FPS médio {fps:0}");
        if (sess.Avg(x => x.GpuLoad) is double g) parts.Add($"GPU média {g:0} %");
        if (sess.Avg(x => x.CpuTotal) is double c) parts.Add($"CPU média {c:0} %");
        if (sess.Avg(x => x.CpuMaxCore) is double k) parts.Add($"núcleo mais ocupado {k:0} %");
        if (sess.Max(x => x.VramPct) is double v) parts.Add($"VRAM máx. {v:0} %");
        if (sess.Max(x => x.GpuTemp) is double gt) parts.Add($"GPU máx. {gt:0} °C");
        ResultSummary.Text = string.Join(" · ", parts);
        Distribution.ItemsSource = sess.Distribution()
            .Select(d => new MeterItem($"{BottleneckAnalyzer.Title(d.Limiter)} — % do tempo", $"{d.Pct:0} %", d.Pct / 100)).ToList();
        ResultAdvice.Text = BottleneckAnalyzer.Advice(main);
        Result.IsVisible = true;
    }
}
