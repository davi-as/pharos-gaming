using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PharosGaming;

public sealed record GuideStep(string Number, string Title, string Where, string Text);

/// <summary>Página Overclock (PHAROS OC): ajustes da GPU, verificador de estabilidade e guia de overclock na BIOS.</summary>
public partial class OcPage : UserControl
{
    private GpuInfo? _gpu;
    private uint? _pendingCap, _appliedCap;
    private CancellationTokenSource? _cts;
    private string _cpuName = SystemInfo.ParseCpuInfo(Sys.Read("/proc/cpuinfo") ?? "").Model;

    public OcPage()
    {
        InitializeComponent();
        BuildGuide();
        UpdateHistory();
        _ = InitAsync();
    }

    private static string? Tag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private async Task InitAsync()
    {
        _gpu = (await Task.Run(GpuInfoCollector.Collect)).FirstOrDefault(g => g.Vendor is "NVIDIA" or "AMD");
        GpuTitle.Text = _gpu != null ? "GPU · " + GameCatalog.ShortGpuName(_gpu.Name).ToUpperInvariant() : "GPU";
        var range = _gpu != null ? OcCheck.PowerRange(_gpu) : null;
        if (_gpu == null || range == null)
        {
            GpuState.Text = "Ajustes disponíveis para placas NVIDIA (nvidia-smi) e AMD (amdgpu).";
            Power.IsEnabled = BtnApply.IsEnabled = BtnReset.IsEnabled = false;
        }
        else
        {
            var (min, max, cur, _) = range.Value;
            Power.Minimum = min; Power.Maximum = max; Power.Value = cur;
            if (_gpu.NvmlHandle != IntPtr.Zero)
            {
                uint top = Nvml.MaxClock(_gpu.NvmlHandle, Nvml.ClockGraphics) ?? 3000;
                Chip("Sem teto", null, true);
                Chip($"Leve · {top - 300} MHz", top - 300, false);
                Chip($"Equilibrado · {top - 500} MHz", top - 500, false);
                Chip($"Econômico · {top - 800} MHz", top - 800, false);
            }
            else ClockLabel.Text = "TETO DE CLOCK: SÓ NVIDIA (na AMD use o limite de energia)";
            UpdateState();
        }
        RecentErrors.Text = await Task.Run(() =>
        {
            var e = OcCheck.ErrorsSince(DateTime.Now.AddDays(-7));
            return e.Error != null ? "Não foi possível ler o log do kernel: " + e.Error
                : e.Total == 0 ? "Nenhum erro de hardware nem travamento da GPU nos últimos 7 dias."
                : $"Últimos 7 dias: {e.Whea} erros de hardware (MCE/PCIe/EDAC) · {e.GpuResets} travamentos da GPU.\nÚltimo: {e.LastDetail}";
        });

        void Chip(string label, uint? cap, bool on)
        {
            var rb = new RadioButton { Content = label, GroupName = "OcClock", IsChecked = on };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) _pendingCap = cap; };
            ClockPanel.Children.Add(rb);
        }
    }

    private void UpdateState()
    {
        if (_gpu == null || OcCheck.PowerRange(_gpu) is not { } r) return;
        string clock = _gpu.NvmlHandle != IntPtr.Zero && Nvml.Clock(_gpu.NvmlHandle, Nvml.ClockGraphics) is uint c ? $"{c} MHz" : "—";
        GpuState.Text = $"Limite de energia atual {r.Current:0} W (padrão {r.Default:0} W) · teto de clock: " +
                        (_appliedCap is uint cap ? $"{cap} MHz" : "nenhum") + $" · clock agora: {clock}";
    }

    private void Power_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        PowerValue.Text = $"{Power.Value:0} W";

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (_gpu == null) return;
        GpuResult.Text = "Aguardando a senha de administrador…";
        var r = await Sys.RunRootAsync(OcCheck.ApplyScript(_gpu, Power.Value, _pendingCap));
        if (r.Ok) _appliedCap = _pendingCap;
        GpuResult.Text = r.Ok
            ? $"Energia: {Power.Value:0} W · teto de clock: {(_pendingCap is uint c ? $"{c} MHz" : "removido")}. Rode o verificador para confirmar a estabilidade."
            : "Não aplicado: " + Sys.RootError(r);
        UpdateState();
    }

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (_gpu == null) return;
        GpuResult.Text = "Aguardando a senha de administrador…";
        var r = await Sys.RunRootAsync(OcCheck.ResetScript(_gpu));
        if (r.Ok)
        {
            _appliedCap = null;
            if (OcCheck.PowerRange(_gpu) is { } range) Power.Value = range.Default;
            foreach (var rb in ClockPanel.Children.OfType<RadioButton>()) rb.IsChecked = rb.Content as string == "Sem teto";
        }
        GpuResult.Text = r.Ok ? "GPU de volta ao padrão de fábrica." : "Restauração falhou: " + Sys.RootError(r);
        UpdateState();
    }

    private void UpdateHistory() =>
        History.ItemsSource = AppSettings.Current.BenchRecords.Where(r => r.Test == "oc")
            .Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, r.Score)).ToList();

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (BenchLog.Busy != null) { Status.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        string target = Tag(TargetPanel) ?? "cpu";
        bool cpu = target is "cpu" or "both", gpu = target is "gpu" or "both";
        var duration = TimeSpan.FromMinutes(int.Parse(Tag(DurPanel) ?? "15"));
        string label = (Label.Text ?? "").Trim() is { Length: > 0 } l ? l : "sem descrição";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BenchLog.Busy = "Verificador de estabilidade";
        BtnCheck.Content = "CANCELAR";
        VerdictCard.IsVisible = false;
        TargetPanel.IsEnabled = DurPanel.IsEnabled = false;
        var start = DateTime.Now;
        double? cpuScore = null, gpuScore = null;
        float maxCpu = 0, maxGpu = 0;
        double sumGpuW = 0; int nGpuW = 0;
        string? fail = null;
        var stress = new StressTester();
        var burn = new GpuBurn();
        try
        {
            if (cpu)
            {
                Status.Text = "Medindo a nota da CPU (render multi-core)…";
                var r = await Task.Run(() => new BenchRenderer().Run(Environment.ProcessorCount, TimeSpan.Zero, ct), ct);
                if (!ct.IsCancellationRequested) cpuScore = r.Score;
            }
            if (gpu && !ct.IsCancellationRequested)
            {
                Status.Text = "Medindo a nota da GPU (cena de jogo, 30 s)…";
                var gb = new GpuGameBench { Duration = TimeSpan.FromSeconds(30), AdapterName = _gpu?.Name ?? "" };
                Live.Paused = true;
                using (ct.Register(gb.Cancel))
                {
                    var r = await Task.Factory.StartNew(gb.Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    if (r != null) gpuScore = r.Score;
                    else if (!gb.Cancelled) fail = $"a cena de GPU falhou ({gb.Error})";
                }
                Live.Paused = false;
            }
            if (fail == null && !ct.IsCancellationRequested)
            {
                if (cpu) stress.Start(cpu: true, ram: true);
                if (gpu) burn.Start(new GpuBurnSettings { Width = 1920, Height = 1080, Msaa = 4, Shells = 128, Iterations = 48, AdapterName = _gpu?.Name ?? "" });
                var sw = Stopwatch.StartNew();
                int lastCheck = 0;
                while (sw.Elapsed < duration && !ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, CancellationToken.None);
                    float? ct0 = Live.Cpu?.Temp, gt = Live.Gpu?.Temp;
                    if (ct0 is float a) maxCpu = Math.Max(maxCpu, a);
                    if (gt is float b) maxGpu = Math.Max(maxGpu, b);
                    if (gpu && Live.Gpu?.PowerW is float gw) { sumGpuW += gw; nGpuW++; }
                    Progress.Value = sw.Elapsed / duration;
                    Status.Text = $"Stress {(cpu && gpu ? "CPU + RAM + GPU" : cpu ? "CPU + RAM" : "GPU")} · {sw.Elapsed:mm\\:ss} de {duration:mm\\:ss} · " +
                                  $"máx. CPU {(maxCpu > 0 ? $"{maxCpu:0} °C" : "—")} · máx. GPU {(maxGpu > 0 ? $"{maxGpu:0} °C" : "—")}";
                    if (cpu && ct0 >= 95) { fail = $"CPU chegou a {ct0:0} °C (limite de segurança 95 °C)"; break; }
                    if (gpu && gt >= 88) { fail = $"GPU chegou a {gt:0} °C (limite de segurança 88 °C)"; break; }
                    if (cpu && stress.MemoryErrors > 0) { fail = $"{stress.MemoryErrors} erros de memória (RAM/EXPO instável)"; break; }
                    if (gpu && !burn.Running) { fail = burn.Error != null ? $"a GPU falhou ({burn.Error})" : "o render da GPU parou"; break; }
                    if ((int)sw.Elapsed.TotalSeconds - lastCheck >= 5)
                    {
                        lastCheck = (int)sw.Elapsed.TotalSeconds;
                        var he = await Task.Run(() => OcCheck.ErrorsSince(start));
                        if (he.Total > 0) { fail = $"o kernel registrou {he.Whea} erros de hardware e {he.GpuResets} travamentos da GPU"; break; }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            stress.Stop();
            burn.Stop();
            Live.Paused = false;
            BenchLog.Busy = null;
        }
        _cts = null;
        BtnCheck.Content = "INICIAR VERIFICAÇÃO";
        TargetPanel.IsEnabled = DurPanel.IsEnabled = true;
        Progress.Value = 0;
        if (ct.IsCancellationRequested && fail == null) { Status.Text = "Verificação cancelada."; return; }
        var errs = await Task.Run(() => OcCheck.ErrorsSince(start));
        if (fail == null && errs.Total > 0) fail = $"o kernel registrou {errs.Whea} erros de hardware e {errs.GpuResets} travamentos da GPU";
        bool ok = fail == null;
        Status.Text = $"Concluído em {DateTime.Now - start:mm\\:ss}.";
        Verdict.Text = ok ? "ESTÁVEL" : "INSTÁVEL";
        VerdictWhy.Text = ok ? $"Nenhum erro em {duration.TotalMinutes:0} min de stress. Para Curve Optimizer, use o PC normalmente por um dia e confira os erros recentes."
                             : $"Motivo: {fail}. Volte um passo no ajuste e teste de novo.";
        VerdictCard.Background = ok ? App.Brush("Card") : App.Brush("BadFill");
        Verdict.Foreground = VerdictWhy.Foreground = ok ? App.Brush("Fg") : App.Brush("OnFlame");
        var rows = new List<Kv> { new("Configuração", label) };
        if (cpuScore is double cs) rows.Add(new("Nota CPU (render)", $"{cs:N0} pts"));
        if (gpuScore is double gs) rows.Add(new("Nota GPU (cena)", $"{gs:N0} pts"));
        if (maxCpu > 0) rows.Add(new("Temp. máx. CPU", $"{maxCpu:0} °C"));
        if (maxGpu > 0) rows.Add(new("Temp. máx. GPU", $"{maxGpu:0} °C"));
        if (nGpuW > 0) rows.Add(new("Consumo médio GPU", $"{sumGpuW / nGpuW:0} W"));
        rows.Add(new("Erros de hardware / travamentos da GPU", $"{errs.Whea} / {errs.GpuResets}"));
        Result.ItemsSource = rows;
        VerdictCard.IsVisible = true;
        string summary = (ok ? "estável" : "INSTÁVEL") + " · " + label + (cpuScore is double c1 ? $" · CPU {c1:N0}" : "") + (gpuScore is double g1 ? $" · GPU {g1:N0}" : "");
        AppSettings.Current.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, "oc", ok ? 1.0 : 0.15, summary));
        try { AppSettings.Current.Save(); } catch { }
        UpdateHistory();
    }

    private void BuildGuide()
    {
        var (maker, model) = OcCheck.Motherboard();
        bool giga = maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase), asus = maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        bool msi = maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        string cpu = GameCatalog.ShortCpuName(_cpuName);
        bool ryzen = _cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        string board = ((giga ? "Gigabyte" : msi ? "MSI" : maker.Split(' ').FirstOrDefault() ?? "") + " " + model).Trim();
        GuideTitle.Text = $"GUIA: OVERCLOCK NA BIOS · {board.ToUpperInvariant()} · {cpu.ToUpperInvariant()}";
        GuideIntro.Text = "Faça uma mudança por vez e rode o verificador depois de cada uma. Os menus podem mudar um pouco com a versão da BIOS. Valores são pontos de partida seguros, não garantia: cada chip é diferente.";
        string pbo = giga ? "Tweaker → Advanced CPU Settings → Precision Boost Overdrive  (ou Settings → AMD Overclocking)"
                   : asus ? "Advanced → AMD Overclocking → Precision Boost Overdrive  (ou AI Tweaker → PBO)"
                   : msi ? "OC → Advanced CPU Configuration → AMD Overclocking → Precision Boost Overdrive"
                   : "Advanced → AMD Overclocking → Precision Boost Overdrive";
        string expo = giga ? "Tweaker → Extreme Memory Profile (X.M.P.) → EXPO1" : asus ? "AI Tweaker → Ai Overclock Tuner → EXPO I"
                    : msi ? "OC → A-XMP/EXPO → Profile 1" : "procure EXPO / XMP no menu de memória";
        var list = new List<GuideStep>
        {
            new("0", "Antes de começar", giga ? "Save & Exit → Save Profiles" : "Save & Exit → Save Profile",
                "Salve o perfil atual da BIOS. Se o PC não ligar depois de um ajuste, espere 3 tentativas (muitas placas voltam sozinhas) ou limpe a CMOS (jumper CLR_CMOS na placa ou tirando a bateria por 1 minuto)."),
            new("1", "Ative o perfil da memória (EXPO/XMP)", expo,
                "É o maior ganho em jogos para a maioria dos PCs: a RAM sai do padrão lento e vai para a velocidade da etiqueta. Teste: verificador \"CPU + RAM\" por 15 min (o teste de RAM acusa erro se o perfil não for estável). A página Sistema mostra se o perfil parece ativo."),
        };
        if (ryzen)
        {
            list.Add(new("2", "Precision Boost Overdrive (PBO)", pbo, "Precision Boost Overdrive → Advanced. PBO Limits → Motherboard. Isso libera os limites de energia para o boost durar mais. Teste com o verificador."));
            list.Add(new("3", "Curve Optimizer (undervolt da CPU)", pbo + " → Curve Optimizer",
                $"All Cores → Negative → 10. Teste. Se passar, tente 15, 20, 25… até falhar; volte 5 a partir de onde falhou. No {cpu} costuma ficar entre -15 e -30. Instabilidade de Curve Optimizer aparece muito em uso leve: use o PC normalmente por um dia e veja \"Erros de hardware recentes\" aqui (erro de MCE = volte um passo)."));
            list.Add(new("4", "Max CPU Boost Clock Override (opcional)", pbo, "Depois do Curve Optimizer estável: +100 MHz, teste; depois +200 MHz. Ganho pequeno; pode exigir reduzir o Curve Optimizer."));
            list.Add(new("5", "Alternativa fria: Eco Mode", pbo + " → Eco Mode", "Em vez de mais desempenho: limita a CPU a 65 W (ou 105 W). Perde ~5–10% em tarefas pesadas multi-core, quase nada em jogos, e roda bem mais frio e silencioso."));
        }
        else
            list.Add(new("2", "Processador Intel", "Tweaker / Ai Tweaker / OC → CPU", "Para Intel, prefira undervolt (offset negativo de tensão) e limites de energia (PL1/PL2) em vez de aumentar multiplicadores. Teste cada passo com o verificador."));
        Guide.ItemsSource = list;
    }
}
