using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Overlay: o que o MangoHud mostra por cima do jogo, posição e tamanho (arquivo de configuração do PHAROS).</summary>
public partial class OverlayPage : UserControl
{
    private readonly AppSettings _s = AppSettings.Current;
    private HashSet<string> _show;
    private bool _loading = true;

    public OverlayPage()
    {
        InitializeComponent();
        _show = _s.OverlayItems is { } l ? l.ToHashSet() : OverlayMetrics.Defaults();
        ChkEnabled.IsChecked = _s.OverlayEnabled;
        ChkVisible.IsChecked = _s.OverlayVisible;
        foreach (var rb in CornerPanel.Children.OfType<RadioButton>()) rb.IsChecked = rb.Tag as string == _s.OverlayCorner;
        foreach (var rb in ScalePanel.Children.OfType<RadioButton>())
            rb.IsChecked = Math.Abs(double.Parse((string)rb.Tag!, CultureInfo.InvariantCulture) - _s.OverlayScale) < 0.01;
        ToolNote.Text = Sys.Has("mangohud") ? "" : "O MangoHud não está instalado (pacote mangohud da sua distro): sem ele não há overlay nem FPS no painel.";
        BuildPicker();
        _loading = false;
        Save();
    }

    /// <summary>Caixas de seleção agrupadas por componente.</summary>
    private void BuildPicker()
    {
        Items.Children.Clear();
        foreach (var group in OverlayMetrics.All.GroupBy(m => m.Group))
        {
            Items.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "label" }, Margin = new(0, 10, 0, 4) });
            var wrap = new WrapPanel();
            foreach (var m in group)
            {
                var cb = new CheckBox { Content = m.Label, IsChecked = _show.Contains(m.Key), Margin = new(0, 0, 18, 4) };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) _show.Add(m.Key); else _show.Remove(m.Key);
                    Save();
                };
                wrap.Children.Add(cb);
            }
            Items.Children.Add(wrap);
        }
    }

    private void Preset_Click(object? sender, RoutedEventArgs e)
    {
        _show = (sender as Control)?.Tag as string switch
        {
            "all" => OverlayMetrics.All.Select(m => m.Key).ToHashSet(),
            "min" => new HashSet<string> { "fps", "frametime", "low1" },
            _ => OverlayMetrics.Defaults(),
        };
        _loading = true;
        BuildPicker();
        _loading = false;
        Save();
    }

    private void Enabled_Changed(object? sender, RoutedEventArgs e) => Save();

    private void Option_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: not true }) return;
        Save();
    }

    private void Save()
    {
        if (_loading) return;
        _s.OverlayEnabled = ChkEnabled.IsChecked == true;
        _s.OverlayVisible = ChkVisible.IsChecked == true;
        _s.OverlayCorner = CornerPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "TL";
        _s.OverlayScale = double.Parse(ScalePanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "1", CultureInfo.InvariantCulture);
        _s.OverlayItems = OverlayMetrics.All.Select(m => m.Key).Where(_show.Contains).ToList();
        try
        {
            _s.Save();
            MangoHudConfig.Write(_s);
            FileNote.Text = $"Arquivo: {MangoHudConfig.FilePath}. O MangoHud relê o arquivo sozinho: as mudanças aparecem com o jogo aberto.";
        }
        catch (Exception ex) { FileNote.Text = "Não consegui gravar a configuração: " + ex.Message; }
        LaunchLine.Text = MangoHudFrames.LaunchOption;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(LaunchLine.Text);
    }
}
