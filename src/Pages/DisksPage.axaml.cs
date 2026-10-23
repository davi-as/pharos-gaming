using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Discos: saúde de cada disco físico, com leitura completa do S.M.A.R.T. sob demanda (root).</summary>
public partial class DisksPage : UserControl
{
    /// <summary>Última leitura completa (smartctl), reaproveitada em ATUALIZAR e no relatório.</summary>
    public static Dictionary<string, string>? LastSmart { get; private set; }

    public DisksPage()
    {
        InitializeComponent();
        if (!Sys.Has("smartctl"))
        {
            BtnFull.IsEnabled = false;
            ToolTip.SetTip(BtnFull, "Instale o pacote smartmontools para a leitura completa.");
        }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        BtnRefresh.IsEnabled = false;
        Status.Text = "Lendo os discos…";
        try
        {
            var smart = LastSmart;
            var cards = await Task.Run(() => DiskHealth.Read(smart));
            Cards.ItemsSource = cards;
            int bad = cards.Count(c => c.Verdict is Verdict.Warning or Verdict.Critical);
            Status.Text = cards.Count == 0 ? "Nenhum disco encontrado."
                : (bad == 0 ? $"{cards.Count} disco(s) · nenhum problema encontrado" : $"{cards.Count} disco(s) · {bad} precisa(m) de atenção")
                  + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { Status.Text = "Falhou: " + ex.Message; }
        finally { BtnRefresh.IsEnabled = true; }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Full_Click(object? sender, RoutedEventArgs e)
    {
        BtnFull.IsEnabled = false;
        Status.Text = "Aguardando a senha de administrador…";
        try
        {
            var devices = await Task.Run(() => SystemInfo.ReadBlockDevices()
                .Where(d => d.Type == "disk" && d.Size > 0 && !d.Name.StartsWith("zram") && !d.Name.StartsWith("loop")).Select(d => "/dev/" + d.Name).ToList());
            var r = await Sys.RunRootAsync(DiskHealth.SmartctlScript(devices));
            if (r.Denied) { Status.Text = Sys.RootError(r); return; }
            LastSmart = DiskHealth.SplitSmartctl(r.Out);
            await RefreshAsync();
        }
        finally { BtnFull.IsEnabled = Sys.Has("smartctl"); }
    }
}
