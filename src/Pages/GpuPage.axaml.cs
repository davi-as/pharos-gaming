using Avalonia.Controls;

namespace PharosGaming;

/// <summary>Página Placa de vídeo: especificações completas (como o GPU-Z) e sensores ao vivo.</summary>
public partial class GpuPage : UserControl
{
    private List<GpuInfo> _gpus = new();
    private GpuInfo? _gpu;
    private int _rowsShown = -1;

    public GpuPage()
    {
        InitializeComponent();
        Live.Tick += UpdateLive;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { _gpus = await Task.Run(GpuInfoCollector.Collect); }
        catch (Exception ex) { Sub.Text = "Não foi possível ler a placa de vídeo: " + ex.Message; return; }
        Sub.Text = _gpus.Count == 0 ? "Nenhuma placa de vídeo encontrada pelo lspci." : "Especificações completas e sensores em tempo real";
        for (int i = 0; i < _gpus.Count && _gpus.Count > 1; i++)
        {
            var g = _gpus[i];
            var chip = new RadioButton { Content = g.Name, GroupName = "Gpu", IsChecked = i == 0, Margin = new(10, 0, 0, 0) };
            chip.IsCheckedChanged += (_, _) => { if (chip.IsChecked == true) Show(g); };
            Chips.Children.Add(chip);
        }
        if (_gpus.Count > 0) Show(_gpus[0]);
    }

    private void Show(GpuInfo g)
    {
        _gpu = g;
        GpuName.Text = g.Name;
        GpuSubtitle.Text = g.Subtitle;
        GpuVendor.Text = g.Vendor;
        Sections.ItemsSource = g.Sections;
        _rowsShown = -1;
        UpdateLive();
    }

    private void UpdateLive()
    {
        if (_gpu == null || !IsVisible) return;
        SensorMonitor.Shared.Refresh();
        Extras.ItemsSource = _gpu.LiveExtras();
        var rows = SensorMonitor.Shared.Rows.Where(r => r.Hardware == _gpu.SensorHardware).ToList();
        if (rows.Count == _rowsShown) return; // as linhas se atualizam sozinhas; só refaz a lista quando surge sensor novo
        _rowsShown = rows.Count;
        LiveRows.ItemsSource = rows.OrderBy(r => r.Type).ThenBy(r => r.Name).ToList();
    }
}
