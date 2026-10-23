using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Otimização (PHAROS OTIMIZA): limpeza de memória, programas para fechar e ajustes reversíveis do sistema.</summary>
public partial class OptimizePage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly List<Tweak> _tweaks = LinuxTweaks.All();
    private List<BackgroundApp> _apps = new();
    private bool _confirm;

    public OptimizePage()
    {
        InitializeComponent();
        foreach (var t in _tweaks)
            try { t.Refresh(); } catch (Exception ex) { t.Status = $"Não foi possível ler: {ex.Message}"; }
        Tweaks.ItemsSource = _tweaks;
        Live.Tick += UpdateRam;
        UpdateRam();
        _ = RefreshAppsAsync();
    }

    private void UpdateRam()
    {
        if (!IsVisible && RamText.Text != null) return;
        var (total, avail) = Optimizer.MemoryStatus();
        if (total == 0) return;
        double used = total - avail;
        RamText.Text = $"{used / 1073741824.0:0.0} de {total / 1073741824.0:0.0} GB · {used / total:P0}";
        RamBar.Value = used / total;
    }

    private async Task RefreshAppsAsync()
    {
        _apps = await Task.Run(Optimizer.ListApps);
        Apps.ItemsSource = _apps;
        AppsEmpty.IsVisible = _apps.Count == 0;
    }

    private async void RefreshApps_Click(object? sender, RoutedEventArgs e) => await RefreshAppsAsync();

    private async void Optimize_Click(object? sender, RoutedEventArgs e)
    {
        var toClose = _apps.Where(a => a.Selected).ToList();
        if (!_confirm)
        {
            // sem caixa de diálogo: o segundo clique confirma
            _confirm = true;
            Confirm.IsVisible = true;
            Confirm.Text = "O PHAROS OTIMIZA vai devolver ao sistema o cache de disco guardado na RAM (pede a senha) e fechar: " +
                           (toClose.Count == 0 ? "nenhum programa marcado" : string.Join(", ", toClose.Select(a => a.Title))) +
                           ". Salve o que estiver aberto nesses programas. Clique de novo para confirmar.";
            BtnOptimize.Content = "Confirmar";
            return;
        }
        _confirm = false;
        Confirm.IsVisible = false;
        BtnOptimize.IsEnabled = false;
        BtnOptimize.Content = "Otimizando…";
        Result.Text = "";
        var closeResult = await Task.Run(() => Optimizer.CloseApps(toClose));
        var mem = await Optimizer.CleanMemory(); // depois de fechar, para contar a memória liberada por eles também
        BtnOptimize.IsEnabled = true;
        BtnOptimize.Content = "PHAROS otimiza";
        var parts = new List<string> { $"Memória livre a mais: {mem.FreedBytes / 1048576.0:N0} MB" };
        parts.Add(mem.Dropped ? "cache de disco devolvido" : $"cache não limpo ({mem.Error})");
        if (closeResult.Closed.Count > 0) parts.Add($"fechados: {string.Join(", ", closeResult.Closed)}");
        if (closeResult.NotClosed.Count > 0) parts.Add($"não fecharam (podem ter algo não salvo ou pedir confirmação): {string.Join(", ", closeResult.NotClosed)}");
        Result.Text = string.Join(" · ", parts) + ".";
        await RefreshAppsAsync();
        UpdateRam();
    }

    private async void Tweak_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: Tweak t } sw) return;
        bool turnOn = sw.IsChecked == true;
        sw.IsEnabled = false;
        t.Status = t.NeedsRoot ? "Aguardando a senha de administrador…" : "Aplicando…";
        string? err;
        try { err = await t.Write(turnOn, _settings); }
        catch (Exception ex) { err = ex.Message; }
        try { _settings.Save(); } catch { }
        t.Refresh();
        t.Status = err != null ? $"Falhou: {err}" : t.IsOn == turnOn ? (turnOn ? "Aplicado." : "Desligado.") : "O sistema não aceitou a mudança.";
        sw.IsChecked = t.IsOn; // volta ao estado real
        sw.IsEnabled = true;
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings.TweakBackup.Count == 0) { TweaksResult.Text = "Nenhum ajuste foi alterado pelo PHAROS GAMING ainda."; return; }
        TweaksResult.Text = "Restaurando…";
        string? err = await LinuxTweaks.RestoreAll(_tweaks, _settings);
        try { _settings.Save(); } catch { }
        foreach (var t in _tweaks) { t.Refresh(); t.Status = err == null ? "Restaurado." : ""; }
        TweaksResult.Text = err ?? "Ajustes de volta como estavam antes do PHAROS GAMING.";
    }
}
