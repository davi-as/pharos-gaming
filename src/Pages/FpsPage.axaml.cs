using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página FPS estimado: quanto FPS esperar em jogos populares (HowManyFPS ou estimativa local pelo hardware).</summary>
public partial class FpsPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly HowManyFpsClient _api = new();
    private readonly List<GameRow> _rows = GameCatalog.Games.Select(g => new GameRow(g)).ToList();
    private CancellationTokenSource? _fetch;
    private string _cpuName = "", _gpuName = "";
    private double _cpuIdx = 80, _gpuIdx = 50;
    private bool _ready;

    public FpsPage()
    {
        InitializeComponent();
        _api.ApiKey = _settings.HowManyFpsKey;
        if (_api.Configured) ApiKey.Text = _settings.HowManyFpsKey;
        List.ItemsSource = _rows;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        _cpuName = SystemInfo.ParseCpuInfo(Sys.Read("/proc/cpuinfo") ?? "").Model;
        _gpuName = (await Task.Run(GpuInfoCollector.Collect)).FirstOrDefault()?.Name ?? "";
        _gpuIdx = GameCatalog.GpuPerf(_gpuName) ?? 50;
        _cpuIdx = GameCatalog.CpuPerf(_cpuName) ?? 80;
        Sub.Text = $"FPS esperado no seu PC · {GameCatalog.ShortGpuName(_gpuName)} · {GameCatalog.ShortCpuName(_cpuName)}";
        _ready = true;
        Refresh();
    }

    private static string? Tag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        if (_ready && sender is RadioButton { IsChecked: true }) Refresh();
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        string q = (Search.Text ?? "").Trim();
        List.ItemsSource = q.Length == 0 ? _rows : _rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Key_Click(object? sender, RoutedEventArgs e)
    {
        _settings.HowManyFpsKey = string.IsNullOrWhiteSpace(ApiKey.Text) ? null : ApiKey.Text.Trim();
        try { _settings.Save(); } catch { }
        _api.ApiKey = _settings.HowManyFpsKey;
        Refresh();
    }

    /// <summary>Recalcula a estimativa local de todos os jogos e, com chave configurada, busca os dados reais na API.</summary>
    private void Refresh()
    {
        string res = Tag(ResPanel) ?? "1920x1080", preset = Tag(PresetPanel) ?? "Alto", up = Tag(UpPanel) ?? "Native";
        foreach (var row in _rows)
        {
            var est = GameCatalog.Estimate(row.Game, _gpuIdx, _cpuIdx, res, preset, up);
            string limiter = est.Limiter switch
            {
                Bottleneck.Cpu => "gargalo: CPU",
                Bottleneck.Gpu => "gargalo: GPU",
                Bottleneck.Engine => $"trava do jogo em {row.Game.EngineCap:0} FPS",
                _ => ""
            };
            row.Set(est.Avg, 0, "ESTIMATIVA", limiter);
        }

        _fetch?.Cancel();
        if (_api.Configured)
        {
            SourceTitle.Text = "Fonte: HowManyFPS (dados reais)";
            SourceText.Text = "FPS médio e 1% low medidos em sessões reais de jogo (no Windows). Respostas ficam em cache por 7 dias. " +
                              "A API usa as configurações gráficas dela; \"Gráficos\" vale só para a estimativa.";
            BtnKey.Content = "ATUALIZAR CHAVE";
            _fetch = new CancellationTokenSource();
            _ = FetchAsync(res, up, _fetch.Token);
        }
        else
        {
            SourceTitle.Text = "Fonte: estimativa local (aproximada)";
            SourceText.Text = "Calculada a partir do desempenho relativo da sua GPU e CPU. Para FPS medido em jogo, " +
                              "peça uma chave da API em info@howmanyfps.com e cole ao lado.";
            BtnKey.Content = "SALVAR CHAVE";
        }
    }

    private async Task FetchAsync(string res, string up, CancellationToken ct)
    {
        string cpu = GameCatalog.ShortCpuName(_cpuName), gpu = GameCatalog.ShortGpuName(_gpuName);
        int ok = 0;
        foreach (var row in _rows)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var r = await _api.GetAsync(cpu, gpu, row.Name, res, up, ct);
                row.Set(r.AvgFps, r.Min1Fps, "HOWMANYFPS", "");
                ok++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                row.Set(row.Fps, 0, "ESTIMATIVA", $"API: {ex.Message}");
                if (ex.Message.Contains("chave") || ex.Message.Contains("cota"))
                {
                    SourceText.Text = $"A API recusou a consulta ({ex.Message}). Mostrando a estimativa local.";
                    return; // não adianta insistir nos outros jogos
                }
            }
        }
        if (!ct.IsCancellationRequested)
            SourceText.Text = $"{ok} de {_rows.Count} jogos com dados da HowManyFPS; os demais usam a estimativa local.";
    }
}
