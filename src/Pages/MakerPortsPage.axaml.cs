using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Portas seriais (PHAROS MAKER): placas conectadas, permissão, ModemManager, brltty e programas que prendem a porta.</summary>
public partial class MakerPortsPage : UserControl
{
    private List<SerialPortInfo> _ports = new();
    private string _group = SerialPorts.DefaultGroup();

    public MakerPortsPage()
    {
        InitializeComponent();
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        _ports = await Task.Run(SerialPorts.List);
        Ports.ItemsSource = _ports;
        Empty.IsVisible = _ports.Count == 0;
        _group = _ports.FirstOrDefault(p => !p.UserHasAccess)?.Group ?? _ports.FirstOrDefault()?.Group ?? SerialPorts.DefaultGroup();
        bool inGroup = await Task.Run(() => SerialPorts.UserGroups().Contains(_group));
        AccessText.Text = inGroup ? $"Seu usuário já está no grupo \"{_group}\": tem acesso às portas seriais."
                                  : $"Seu usuário não está no grupo \"{_group}\", dono das portas seriais: as IDEs dão \"Permission denied\" ao enviar código.";
        BtnAccess.IsVisible = !inGroup;
        bool mm = await Task.Run(SerialPorts.ModemManagerActive);
        ModemText.Text = mm ? "O ModemManager está ligado: ele \"sonda\" cada placa nova achando que é um modem 3G e pode quebrar o envio de código nos primeiros segundos." : "ModemManager desligado: não interfere nas placas.";
        BtnModem.IsVisible = mm;
        bool brl = await Task.Run(SerialPorts.BrlttyPresent);
        BrlttyText.Text = brl ? "O brltty (leitor de tela braille) está instalado: ele captura conversores CH340 e a porta /dev/ttyUSB some logo depois de plugar." : "";
        BtnBrltty.IsVisible = brl;
        BtnHolders.IsVisible = _ports.Any(p => p.Holders.Count > 0);
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) { Result.Text = ""; await ScanAsync(); }

    private async Task Root(string script, string ok)
    {
        Result.Text = "Aguardando a senha de administrador…";
        var r = await Sys.RunRootAsync(script);
        Result.Text = r.Ok ? ok : "Falhou: " + Sys.RootError(r);
        await ScanAsync();
    }

    private async void Access_Click(object? sender, RoutedEventArgs e) =>
        await Root(SerialPorts.AddToGroupScript(_group), $"Pronto: seu usuário entrou no grupo \"{_group}\". Saia e entre de novo na sessão (ou reinicie) para valer.");

    private async void Modem_Click(object? sender, RoutedEventArgs e) =>
        await Root(SerialPorts.DisableModemManagerScript, "ModemManager desligado. Se usar modem 3G/4G USB, religue com: systemctl enable --now ModemManager");

    private async void Brltty_Click(object? sender, RoutedEventArgs e) =>
        await Root(SerialPorts.DisableBrlttyScript, "O brltty não captura mais os conversores. Replugue a placa.");

    private async void Holders_Click(object? sender, RoutedEventArgs e)
    {
        var pids = _ports.SelectMany(p => p.Holders).Select(h => h.Pid).Distinct().ToList();
        foreach (int pid in pids) await Sys.RunAsync("kill", "-TERM", pid.ToString());
        await Task.Delay(800);
        Result.Text = $"{pids.Count} programa(s) avisados para fechar.";
        await ScanAsync();
    }
}
