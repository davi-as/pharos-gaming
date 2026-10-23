using Avalonia.Controls;

namespace PharosGaming;

public sealed record SensorGroup(string Hardware, List<SensorRow> Rows);

/// <summary>Página Sensores: todas as leituras do hardware, com busca, filtro por tipo e mínimo/máximo desde a abertura.</summary>
public partial class SensorsPage : UserControl
{
    private readonly SensorMonitor _monitor = SensorMonitor.Shared;
    private string? _category;
    private int _known = -1;

    public SensorsPage()
    {
        InitializeComponent();
        Live.Tick += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (!IsVisible && _known >= 0) return; // fora da tela não lê (são centenas de arquivos)
        _monitor.Refresh();
        Status.Text = $"{_monitor.Rows.Count} sensores · atualizado {DateTime.Now:HH:mm:ss}";
        if (_monitor.Rows.Count == _known) return;
        // sensor novo: refaz os chips e a lista
        _known = _monitor.Rows.Count;
        Chips.Children.Clear();
        AddChip("Todos", null);
        foreach (var c in _monitor.Rows.Select(r => r.Category).Distinct().OrderBy(x => x)) AddChip(c, c);
        Apply();
    }

    private void AddChip(string label, string? category)
    {
        var chip = new RadioButton { Content = label, GroupName = "SensorCat", IsChecked = category == _category };
        chip.IsCheckedChanged += (_, _) => { if (chip.IsChecked == true) { _category = category; Apply(); } };
        Chips.Children.Add(chip);
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e) => Apply();

    private void Apply()
    {
        string q = (Search.Text ?? "").Trim();
        Groups.ItemsSource = _monitor.Rows
            .Where(r => (_category == null || r.Category == _category)
                        && (q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Hardware.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(r => r.Hardware)
            .Select(g => new SensorGroup(g.Key, g.OrderBy(r => r.Type).ToList()))
            .ToList();
    }
}
