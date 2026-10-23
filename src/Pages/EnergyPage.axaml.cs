using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Energia: bateria (desgaste, ciclos) e fonte, perfil e consumo.</summary>
public partial class EnergyPage : UserControl
{
    public EnergyPage()
    {
        InitializeComponent();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        BtnRefresh.IsEnabled = false;
        Status.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = SensorMonitor.Shared;
            sensors.Refresh();
            var rows = sensors.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(rows));
            Cards.ItemsSource = cards;
            Status.Text = (cards.Count > 1 ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { Status.Text = "Falhou: " + ex.Message; }
        finally { BtnRefresh.IsEnabled = true; }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();
}
