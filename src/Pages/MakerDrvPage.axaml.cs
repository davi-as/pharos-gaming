using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Drivers seriais (PHAROS MAKER): módulos do kernel de cada chip, diagnóstico das placas e Arduino IDE.</summary>
public partial class MakerDrvPage : UserControl
{
    public MakerDrvPage()
    {
        InitializeComponent();
        _ = ScanAsync();
    }

    private async void Scan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        Status.Text = "Verificando…";
        var chips = await Task.Run(() => SerialPorts.Chips.Select(c => (Chip: c, Available: SerialPorts.ModuleAvailable(c.Module), Loaded: SerialPorts.ModuleLoaded(c.Module))).ToList());
        Chips.Items.Clear();
        foreach (var (c, available, loaded) in chips)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new(0, 4) };
            row.Children.Add(new TextBlock { Text = $"{c.Name} · módulo {c.Module}", Classes = { "val" } });
            var state = new TextBlock { Text = !available ? "NÃO ENCONTRADO" : loaded ? "CARREGADO" : "NÃO CARREGADO", Classes = { "label" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new(12, 0) };
            Grid.SetColumn(state, 1);
            row.Children.Add(state);
            if (available && !loaded)
            {
                var b = new Button { Content = "CARREGAR", FontSize = 11 };
                b.Click += async (_, _) =>
                {
                    LoadResult.Text = "Aguardando a senha de administrador…";
                    var r = await Sys.RunRootAsync(SerialPorts.LoadModuleScript(c.Module));
                    LoadResult.Text = r.Ok ? $"Módulo {c.Module} carregado." : "Falhou: " + Sys.RootError(r);
                    await ScanAsync();
                };
                Grid.SetColumn(b, 2);
                row.Children.Add(b);
            }
            Chips.Items.Add(row);
        }
        var ports = await Task.Run(SerialPorts.List);
        NoDevices.IsVisible = ports.Count == 0;
        Devices.ItemsSource = ports.Select(p => new InfoCard
        {
            Title = p.Name, Meta = p.Detail,
            Verdict = p.Driver.Length > 0 ? Verdict.Good : Verdict.Critical, Label = p.Driver.Length > 0 ? "DRIVER OK" : "SEM DRIVER",
            Reasons = p.Driver.Length > 0 ? $"Atendida pelo driver {p.Driver} do kernel." + (p.UserHasAccess ? "" : " Falta permissão: veja Portas seriais.")
                    : p.Chip != null ? $"O chip {p.Chip.Name} usa o módulo {p.Chip.Module}: carregue-o acima. Se não existir, o kernel foi compilado sem ele."
                    : "Chip não reconhecido: procure o modelo do conversor na página do fabricante.",
            Metrics = new() { new("Porta", p.Dev), new("Fabricante", p.Maker), new("Número de série", p.Serial), new("Página oficial", p.Chip?.OfficialUrl ?? "—") },
        }).ToList();
        string? ide = Sys.Has("arduino-ide") ? "Arduino IDE 2 instalada (pacote da distro)." : Sys.Has("arduino-cli") ? "arduino-cli instalado." : null;
        if (ide == null && Sys.Has("flatpak") && (await Sys.RunAsync("flatpak", "info", "cc.arduino.IDE2")).Ok) ide = "Arduino IDE 2 instalada (Flatpak).";
        Ide.Text = ide ?? "Arduino IDE não encontrada.";
        BtnIde.IsVisible = ide == null;
        Status.Text = $"Verificado às {DateTime.Now:HH:mm}.";
    }

    private async void Ide_Click(object? sender, RoutedEventArgs e)
    {
        var app = AppInstaller.Catalog().Where(a => a.Name == "Arduino IDE").ToList();
        await AppInstaller.Detect(app);
        if (!app[0].CanSelect) { IdeResult.Text = app[0].Status; return; }
        IdeResult.Text = "Instalando…";
        await AppInstaller.Install(app, line => { }, CancellationToken.None);
        IdeResult.Text = app[0].Status;
        await ScanAsync();
    }

    private void Cache_Click(object? sender, RoutedEventArgs e)
    {
        var dirs = SerialPorts.ArduinoCaches().ToList();
        long bytes = 0;
        foreach (var d in dirs)
        {
            try
            {
                bytes += new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                Directory.Delete(d, recursive: true);
            }
            catch { }
        }
        IdeResult.Text = dirs.Count == 0 ? "Nenhum cache da IDE encontrado."
            : $"Cache apagado ({Sys.Bytes(bytes)}): compilações antigas e downloads parciais. Feche e abra a IDE; bibliotecas e configurações ficaram.";
    }
}
