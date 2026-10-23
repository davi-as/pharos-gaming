using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PharosGaming;

/// <summary>Negrito para os pacotes de driver na lista de atualizações.</summary>
public sealed class BoolBold : IValueConverter
{
    public static readonly BoolBold Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? FontWeight.Bold : FontWeight.Normal;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Página Drivers (PHAROS DRIVER): vídeo, placa-mãe, atualizações do sistema e de firmware, inventário do kernel.</summary>
public partial class DriverPage : UserControl
{
    private BoardInfo _board = new();
    private bool _busy, _confirmUpgrade;

    public DriverPage()
    {
        InitializeComponent();
        _ = ScanAsync();
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        BtnScan.IsEnabled = false;
        try
        {
            Status.Text = "Lendo dispositivos e drivers…"; Progress.Value = 0.1;
            List.ItemsSource = await Task.Run(DriverManager.Inventory);
            Status.Text = "Lendo placa de vídeo e placa-mãe…"; Progress.Value = 0.3;
            var gpus = await Task.Run(DriverManager.ReadGpus);
            _board = await Task.Run(DriverManager.ReadBoard);
            Board.Text = $"{_board.Maker} {_board.Model}\nBIOS {_board.Bios}";
            BtnBoard.IsEnabled = _board.SupportUrl.Length > 0;
            foreach (var g in gpus.Where(g => g.Vendor == "NVIDIA"))
            {
                Status.Text = $"Consultando a NVIDIA para {g.GpuName}…"; Progress.Value = 0.5;
                await DriverManager.CheckNvidiaAsync(g, CancellationToken.None);
            }
            Gpus.ItemsSource = gpus;
            Status.Text = "Procurando atualizações do sistema…"; Progress.Value = 0.7;
            var ups = await DriverManager.SystemUpdates(CancellationToken.None);
            Updates.ItemsSource = ups;
            int drv = ups.Count(u => u.DriverRelated);
            UpdatesSummary.Text = ups.Count == 0 ? "Sistema em dia." : $"{ups.Count} pacotes para atualizar · {drv} de kernel, driver ou firmware (em negrito)";
            BtnUpgrade.IsEnabled = ups.Count > 0;
            Status.Text = "Procurando firmware no LVFS…"; Progress.Value = 0.9;
            var fw = await DriverManager.FirmwareUpdates(CancellationToken.None);
            Firmware.ItemsSource = fw;
            FirmwareSummary.Text = !Sys.Has("fwupdmgr") ? "fwupd não instalado." : fw.Count == 0 ? "Nenhuma atualização de firmware publicada para este PC." : $"{fw.Count} atualizações de firmware disponíveis";
            Status.Text = $"Verificado às {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex) { Status.Text = "Falhou: " + ex.Message; }
        finally
        {
            Progress.Value = 0;
            _busy = false;
            BtnScan.IsEnabled = true;
        }
    }

    private void Board_Click(object? sender, RoutedEventArgs e) => _ = Sys.RunAsync("xdg-open", _board.SupportUrl);

    private async void Upgrade_Click(object? sender, RoutedEventArgs e)
    {
        if (!_confirmUpgrade)
        {
            // sem caixa de diálogo: o segundo clique confirma
            _confirmUpgrade = true;
            UpgradeLog.Text = "Isto atualiza o sistema inteiro (pede a senha). Feche os jogos antes; se o kernel ou o driver de vídeo mudarem, reinicie o PC no final. Clique de novo para confirmar.";
            BtnUpgrade.Content = "Confirmar atualização";
            return;
        }
        _confirmUpgrade = false;
        BtnUpgrade.IsEnabled = false;
        BtnUpgrade.Content = "Atualizando…";
        UpgradeLog.Text = "Aguardando a senha de administrador…";
        var r = await Sys.RunRootAsync(DriverManager.UpgradeScript());
        var tail = (r.Out + "\n" + r.Err).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(8);
        UpgradeLog.Text = (r.Ok ? "Concluído. Reinicie o PC se o kernel ou o driver de vídeo foram atualizados.\n" : "Falhou: " + Sys.RootError(r) + "\n") + string.Join("\n", tail);
        BtnUpgrade.Content = "Atualizar o sistema";
        await ScanAsync();
    }
}
