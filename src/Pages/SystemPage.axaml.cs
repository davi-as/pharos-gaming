using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace PharosGaming;

/// <summary>Página Sistema: hardware e software detectados, unidades montadas e exportação do relatório.</summary>
public partial class SystemPage : UserControl
{
    private List<InfoNode> _info = new();

    public SystemPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _info = await Task.Run(SystemInfo.Collect);
            Categories.ItemsSource = _info;
            Categories.SelectedIndex = 0;
            Sub.Text = $"{Environment.MachineName} · {_info.Count} categorias";
        }
        catch (Exception ex) { Sub.Text = "Não foi possível ler as informações do sistema: " + ex.Message; }
    }

    public IReadOnlyList<InfoNode> Info => _info;

    private async void Categories_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (Categories.SelectedItem is not InfoNode node) return;
        InfoTitle.Text = node.Header;
        Details.ItemsSource = node.Children;
        bool storage = node.Header == "Armazenamento";
        DrivesPanel.IsVisible = storage;
        if (!storage) return;
        var drives = await Task.Run(DriveMap.Read);
        DriveCards.ItemsSource = drives;
        DrivesSummary.Text = $"{drives.Count} unidades · {Sys.Bytes(drives.Sum(d => d.Free))} livres de {Sys.Bytes(drives.Sum(d => d.Total))}";
    }

    private async void Report_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this)!;
        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Exportar relatório",
            SuggestedFileName = $"PHAROS-GAMING-relatorio-{DateTime.Now:yyyyMMdd-HHmm}.html",
            DefaultExtension = "html",
            FileTypeChoices = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Relatório HTML") { Patterns = new[] { "*.html" } },
                new Avalonia.Platform.Storage.FilePickerFileType("Dados JSON") { Patterns = new[] { "*.json" } },
            },
        });
        string? path = file?.TryGetLocalPath();
        if (path == null) return;
        BtnReport.IsEnabled = false;
        BtnReport.Content = "GERANDO…";
        try
        {
            var data = await Report.CollectAsync(_info);
            bool json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(path, json ? ReportBuilder.Json(data) : ReportBuilder.Html(data), Sys.Utf8);
            Sub.Text = "Relatório salvo em " + path;
            _ = Sys.RunAsync("xdg-open", path);
        }
        catch (Exception ex) { Sub.Text = "Não foi possível gerar o relatório: " + ex.Message; }
        finally
        {
            BtnReport.Content = "EXPORTAR RELATÓRIO";
            BtnReport.IsEnabled = true;
        }
    }
}
