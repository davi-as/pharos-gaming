using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Página Instalar apps: catálogo pós-formatação pelo gerenciador de pacotes da distro ou Flatpak.</summary>
public partial class AppsPage : UserControl
{
    private readonly List<InstallableApp> _apps = AppInstaller.Catalog();
    private CancellationTokenSource? _cts;

    public AppsPage()
    {
        InitializeComponent();
        List.ItemsSource = _apps;
        string? mgr = AppInstaller.Manager();
        Manager.Text = (mgr != null ? $"PACOTES DA DISTRO: {mgr.ToUpperInvariant()}" : "DISTRO SEM PACMAN, APT OU DNF") +
                       (Sys.Has("flatpak") ? " · FLATPAK" : " · SEM FLATPAK");
        _ = AppInstaller.Detect(_apps);
    }

    private void AddLog(string line) => Dispatcher.UIThread.Post(() => Log.Text = (Log.Text ?? "") + $"{DateTime.Now:HH:mm:ss}  {line}\n");

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var sel = _apps.Where(a => a.Selected && a.CanSelect).ToList();
        if (sel.Count == 0) { AddLog("Nada selecionado."); return; }
        _cts = new CancellationTokenSource();
        BtnInstall.Content = "CANCELAR";
        try { await AppInstaller.Install(sel, AddLog, _cts.Token); AddLog("Concluído."); }
        catch (OperationCanceledException) { AddLog("Cancelado."); }
        catch (Exception ex) { AddLog("Falhou: " + ex.Message); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BtnInstall.Content = "INSTALAR SELECIONADOS";
        }
    }
}
