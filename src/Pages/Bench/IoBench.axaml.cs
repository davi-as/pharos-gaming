using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Memória e carregamento (latência/banda da RAM e "carregamento de mapa") ou Disco (estilo CrystalDiskMark).</summary>
public partial class IoBench : UserControl
{
    private readonly bool _disk;
    private CancellationTokenSource? _cts;

    public IoBench() : this(false) { }

    public IoBench(bool disk)
    {
        InitializeComponent();
        _disk = disk;
        DiskOptions.IsVisible = disk;
        Note.Text = disk
            ? "Leitura e escrita direto no disco (O_DIRECT + O_DSYNC), sem o cache de RAM do kernel. O arquivo de teste é apagado no final. SSDs com cache SLC caem depois de gravar dezenas de GB."
            : "A RAM é medida em ns e GB/s; o carregamento lê um pacote de 512 MB e mil arquivos de 64 KB da sua pasta pessoal, sem o cache do kernel. Os arquivos de teste são apagados no final.";
        History.ItemsSource = BenchLog.History(t => t == Test);
        if (disk) _ = LoadDrivesAsync();
    }

    private string Test => _disk ? "disk" : "mem";

    private async Task LoadDrivesAsync()
    {
        var drives = await Task.Run(DiskBench.Drives);
        DrivePanel.Children.Clear();
        foreach (var (root, text) in drives)
            DrivePanel.Children.Add(new RadioButton { Content = text, Tag = root, GroupName = "DiskDrive", IsChecked = DrivePanel.Children.Count == 0 });
        if (drives.Count == 0) { Status.Text = "Nenhuma unidade gravável com espaço livre suficiente (1,5 GB)."; BtnRun.IsEnabled = false; }
    }

    private static string? Tag(Panel p) => p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (BenchLog.Busy != null) { Status.Text = $"Termine o teste em andamento ({BenchLog.Busy}) antes."; return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        BenchLog.Busy = _disk ? "Disco" : "Memória e carregamento";
        BtnRun.Content = "CANCELAR";
        DiskOptions.IsEnabled = false;
        Empty.IsVisible = false;
        var rows = new List<Kv>();
        Result.ItemsSource = null;
        (string Phase, double Frac) progress = ("Preparando…", 0);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) =>
        {
            Status.Text = progress.Phase;
            Progress.Value = progress.Frac;
        });
        timer.Start();
        try
        {
            if (_disk)
            {
                string root = Tag(DrivePanel) ?? throw new InvalidOperationException("Escolha uma unidade.");
                long mb = long.Parse(Tag(SizePanel) ?? "1024");
                var r = await Task.Run(() => DiskBench.Run(root, mb, (phase, f) => progress = (phase, f), ct), ct);
                rows.AddRange(new[]
                {
                    new Kv("Leitura sequencial", $"{r.SeqReadMBs:N0} MB/s", "Blocos de 1 MB. Mede copiar arquivos grandes e carregar jogos."),
                    new Kv("Escrita sequencial", $"{r.SeqWriteMBs:N0} MB/s", "Blocos de 1 MB, direto no disco."),
                    new Kv("Leitura 4 KB aleatória (1 por vez)", $"{r.ReadIopsQ1:N0} IOPS", $"Latência média de {r.ReadLatencyUs:0.0} µs. É o que define a sensação de rapidez do sistema e dos programas."),
                    new Kv("Escrita 4 KB aleatória (1 por vez)", $"{r.WriteIopsQ1:N0} IOPS", $"Latência média de {r.WriteLatencyUs:0.0} µs."),
                    new Kv("Leitura 4 KB aleatória (8 em paralelo)", $"{r.ReadIopsQ8:N0} IOPS", "Várias leituras ao mesmo tempo, como ao abrir muitos programas."),
                });
                Result.ItemsSource = rows;
                Status.Text = $"Concluído em {r.Drive} · arquivo de teste ({r.FileMb} MB) apagado";
                BenchLog.Save("disk", r.Score, $"{r.Drive} · {r.SeqReadMBs:N0}/{r.SeqWriteMBs:N0} MB/s · 4K {r.ReadIopsQ1 / 1000:0.#}k IOPS");
            }
            else
            {
                progress = ("Medindo latência da RAM…", 0.05);
                double lat = await Task.Run(() => MemoryBench.MeasureLatencyNs(ct));
                ct.ThrowIfCancellationRequested();
                rows.Add(new Kv("Latência da RAM", $"{lat:0.0} ns", "Acesso aleatório. Menor é melhor; pesa muito em jogos competitivos."));
                Result.ItemsSource = rows.ToList();
                progress = ("Medindo banda da RAM…", 0.2);
                var bw = await Task.Run(() => MemoryBench.MeasureBandwidth(ct));
                ct.ThrowIfCancellationRequested();
                rows.Add(new Kv("Banda de leitura da RAM", $"{bw.Read:0.0} GB/s", "Todas as threads lendo em sequência."));
                rows.Add(new Kv("Banda de cópia da RAM", $"{bw.Copy:0.0} GB/s", "Leitura + escrita."));
                Result.ItemsSource = rows.ToList();
                var load = await Task.Run(() => MemoryBench.MeasureLoading((phase, f) => progress = (phase, 0.35 + f * 0.6), ct));
                rows.Add(new Kv($"Disco ({load.Drive}) · leitura sequencial", $"{load.SeqReadMBs:N0} MB/s", "Pacote de 512 MB, sem cache do kernel."));
                rows.Add(new Kv("Assets pequenos", $"{load.FilesPerSec:N0} arq/s", $"Mil arquivos de 64 KB com 8 threads ({load.SmallFilesMBs:N0} MB/s), como engines modernas."));
                rows.Add(new Kv("Carregamento do mapa", $"{load.MapLoadSeconds:0.00} s", "Pacote + assets: quanto menor, mais rápido o jogo carrega."));
                Result.ItemsSource = rows.ToList();
                Status.Text = "Concluído · arquivos de teste apagados";
                // Nota para a barra do histórico: mais rápido = maior (inverso da latência × velocidade de carga).
                BenchLog.Save("mem", 1000 / lat + 10 / load.MapLoadSeconds, $"{lat:0} ns · {bw.Read:0} GB/s · mapa {load.MapLoadSeconds:0.00} s");
            }
            History.ItemsSource = BenchLog.History(t => t == Test);
        }
        catch (OperationCanceledException) { Status.Text = "Cancelado (arquivos de teste apagados)"; }
        catch (Exception ex) { Status.Text = $"Falhou: {(ex as AggregateException)?.InnerException?.Message ?? ex.Message}"; }
        finally
        {
            timer.Stop();
            Progress.Value = 0;
            _cts = null;
            BenchLog.Busy = null;
            BtnRun.Content = "INICIAR";
            DiskOptions.IsEnabled = true;
        }
    }
}
