using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Inicialização: programas e serviços que abrem junto com a sessão.</summary>
public partial class StartupPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private StartupEntry? _confirmRemove;

    public StartupPage()
    {
        InitializeComponent();
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        List<StartupEntry> list;
        try { list = await Task.Run(StartupManager.List); }
        catch (Exception ex) { Result.Text = $"Não foi possível listar: {ex.Message}"; return; }
        List.ItemsSource = list;
        int on = list.Count(e => e.Enabled);
        Summary.Text = list.Count == 0 ? "NENHUM PROGRAMA NA INICIALIZAÇÃO" : $"{list.Count} ITENS · {on} ATIVADOS · {list.Count - on} DESATIVADOS";
        BtnRestore.IsEnabled = _settings.StartupRemoved.Count > 0;
        BtnRestore.Content = _settings.StartupRemoved.Count > 0 ? $"RESTAURAR REMOVIDOS ({_settings.StartupRemoved.Count})" : "RESTAURAR REMOVIDOS";
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) { Result.Text = ""; await ScanAsync(); }

    private async void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: StartupEntry item } sw) return;
        bool turnOn = sw.IsChecked == true;
        try
        {
            await StartupManager.SetEnabled(item, turnOn);
            Result.Text = $"{item.Name}: {(turnOn ? "ativado" : "desativado")}. Vale na próxima vez que você entrar na sessão.";
        }
        catch (Exception ex) { Result.Text = $"{item.Name}: falhou ({ex.Message})."; }
        await ScanAsync();
    }

    private async void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: StartupEntry item }) return;
        if (_confirmRemove != item)
        {
            // sem caixa de diálogo: o segundo clique confirma
            _confirmRemove = item;
            Result.Text = $"Remover \"{item.Name}\" da inicialização? O programa continua instalado, só deixa de abrir sozinho, e dá para desfazer em " +
                          "\"Restaurar removidos\". Se só quiser testar, prefira desligar o interruptor. Clique em REMOVER de novo para confirmar.";
            return;
        }
        _confirmRemove = null;
        try
        {
            _settings.StartupRemoved.Add(StartupManager.Remove(item));
            _settings.Save();
            Result.Text = $"{item.Name} removido da inicialização.";
        }
        catch (Exception ex) { Result.Text = $"{item.Name}: não foi possível remover ({ex.Message})."; }
        await ScanAsync();
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        var failed = new List<string>();
        foreach (var b in _settings.StartupRemoved.ToList())
        {
            try { StartupManager.Restore(b); _settings.StartupRemoved.Remove(b); }
            catch (Exception ex) { failed.Add($"{b.Name} ({ex.Message})"); }
        }
        try { _settings.Save(); } catch { }
        Result.Text = failed.Count == 0 ? "Itens removidos devolvidos à inicialização." : "Não restaurados: " + string.Join("; ", failed);
        await ScanAsync();
    }
}
