using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace PharosGaming;

/// <summary>
/// Página PC como console: cada cartão é um pedaço do ecossistema do dotfiles (controle, Hydra, Steam, TV, Bluetooth,
/// MangoHud) com o estado atual e o que dá para mudar. A lógica e os arquivos ficam em <see cref="ConsoleSetup"/>.
/// </summary>
public partial class ConsolePage : UserControl
{
    private readonly Dictionary<string, ComboBox> _gesture = new();
    private bool _loading = true;
    private bool _steamConfirm;

    public ConsolePage()
    {
        InitializeComponent();
        BuildGestures();
        HoldSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) HoldText.Text = $"{HoldSlider.Value:0.#} s"; };
        HydraBoot.IsCheckedChanged += (_, _) => Toggle(() => ConsoleSetup.EditFile(ConsoleSetup.AutostartFile,
            t => ConsoleSetup.SetLine(t, ConsoleSetup.IsHydraAutostart, "--", HydraBoot.IsChecked == true)), HydraNote, "Vale no próximo login.");
        HydraMango.IsCheckedChanged += (_, _) => Toggle(() => ConsoleSetup.EditFile(ConsoleSetup.HydraWrapper,
            t => ConsoleSetup.SetLine(t, ConsoleSetup.IsMangoExport, "#", HydraMango.IsChecked == true)), HydraNote, "Vale quando o Hydra abrir de novo.");
        SteamMango.IsCheckedChanged += (_, _) => Toggle(() => ConsoleSetup.EditFile(ConsoleSetup.AutostartFile,
            t => ConsoleSetup.SetSteamMango(t, SteamMango.IsChecked == true)), SteamNote, "Vale quando a Steam abrir de novo (próximo login ou Reiniciar a Steam).");
        MangoHidden.IsCheckedChanged += (_, _) => Toggle(SetMangoHidden, MangoNote, "Vale no próximo jogo (e no aberto, que relê o arquivo).");
        MangoLog.IsCheckedChanged += (_, _) => Toggle(() =>
        {
            ConsoleSetup.EditFile(MangoHudConfig.FilePath, t => ConsoleSetup.SetPharosLog(t, MangoLog.IsChecked == true));
            FrameLimiter.RewriteAll(AppSettings.Current);
        }, MangoNote, "Pronto: a Visão geral, o Gargalo e o Limitador recebem o FPS de qualquer jogo com MangoHud.");
        BtSwitch.IsCheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            BtNote.Text = await ConsoleSetup.SetService(ConsoleSetup.BluetoothService, BtSwitch.IsChecked == true) ?? "";
        };
        TvAuto.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            AppSettings.Current.AutoTvProfile = TvAuto.IsChecked == true;
            AppSettings.Current.Save();
        };
        TvScreenBox.SelectionChanged += (_, _) =>
        {
            if (_loading || TvScreenBox.SelectedItem is not string item) return;
            AppSettings.Current.TvScreen = item.Split(' ')[0];
            AppSettings.Current.Save();
        };
        _ = Refresh();
        _ = RunDoctor();
    }

    private void BuildGestures()
    {
        foreach (var g in ConsoleSetup.Gestures)
        {
            var row = new Grid { ColumnDefinitions = new("*,230") };
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = g.Label, Classes = { "val" } });
            label.Children.Add(new TextBlock { Text = g.Hint, Classes = { "dim" }, FontSize = 11 });
            var combo = new ComboBox
            {
                ItemsSource = ConsoleSetup.Actions.Select(a => a.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(label);
            row.Children.Add(combo);
            Gestures.Children.Add(row);
            _gesture[g.Key] = combo;
        }
    }

    /// <summary>Lê tudo de novo (os processos e o systemctl fora da thread da interface).</summary>
    private async Task Refresh()
    {
        _loading = true;
        var pad = await ConsoleSetup.Service(ConsoleSetup.ControllerService);
        var bt = await ConsoleSetup.Service(ConsoleSetup.BluetoothService);
        var (hydra, steam, pads) = await Task.Run(() => (ConsoleSetup.Hydra(), ConsoleSetup.Steam(),
            ConsoleSetup.Controllers(Sys.Read("/proc/bus/input/devices") ?? "")));
        string autostart = Sys.Read(ConsoleSetup.AutostartFile) ?? "";
        string wrapper = Sys.Read(ConsoleSetup.HydraWrapper) ?? "";

        // controle
        PadState.Text = !pad.Exists ? "serviço não instalado" : pad.Active ? "ativo" : "parado";
        PadBadge.Classes.Set("alert", pad.Exists && !pad.Active);
        PadList.Text = pads.Count > 0 ? "Conectado: " + string.Join(", ", pads) : "Nenhum controle conectado agora.";
        var b = ConsoleSetup.LoadButtons();
        foreach (var g in ConsoleSetup.Gestures)
            _gesture[g.Key].SelectedIndex = Math.Max(0, Array.FindIndex(ConsoleSetup.Actions, a => a.Id == b[g.Key]));
        VolSwitch.IsChecked = b["GUIDE_VOLUME"] != "0";
        HoldSlider.Value = double.TryParse(b["HOLD_SECONDS"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 2;
        HoldText.Text = $"{HoldSlider.Value:0.#} s";
        BtnSavePad.IsEnabled = pad.Exists;

        // Hydra e Steam
        HydraState.Text = hydra.Running ? "aberto" : "fechado";
        HydraBoot.IsEnabled = ConsoleSetup.LineState(autostart, ConsoleSetup.IsHydraAutostart, "--") is not null;
        HydraBoot.IsChecked = ConsoleSetup.LineState(autostart, ConsoleSetup.IsHydraAutostart, "--") == true;
        HydraMango.IsEnabled = ConsoleSetup.LineState(wrapper, ConsoleSetup.IsMangoExport, "#") is not null;
        HydraMango.IsChecked = ConsoleSetup.LineState(wrapper, ConsoleSetup.IsMangoExport, "#") == true;
        HydraNote.Text = HydraBoot.IsEnabled ? "" : $"Sem a linha do Hydra em {ConsoleSetup.AutostartFile}.";
        SteamState.Text = !steam.Running ? "fechada" : steam.Mango ? "aberta com MangoHud" : "aberta sem MangoHud";
        SteamMango.IsEnabled = autostart.Split('\n').Any(l => ConsoleSetup.IsSteamAutostart(l.Trim()));
        SteamMango.IsChecked = ConsoleSetup.SteamMango(autostart);
        SteamNote.Text = steam.Running && steam.Mango != SteamMango.IsChecked
            ? "A Steam aberta agora não está como a configuração: reinicie para valer." : "";

        // TV, Bluetooth, MangoHud
        bool hasTv = File.Exists(ConsoleSetup.Bin("tv-power"));
        TvButtons.IsEnabled = hasTv;
        string? ip = File.Exists(ConsoleSetup.MachineFile) && ConsoleSetup.ParseEnv(File.ReadAllText(ConsoleSetup.MachineFile)).TryGetValue("TV_IP", out var v) ? v : null;
        TvText.Text = hasTv ? $"Liga junto com o PC pelo controle e desliga ao suspender{(ip != null ? $" · TV em {ip}" : "")}." : "tv-power não instalado.";
        _ = TvStatus(hasTv);
        var screens = await Task.Run(FrameLimiter.Screens);
        string? tv = FrameLimiter.TvName(AppSettings.Current, screens);
        TvScreenBox.ItemsSource = screens.Select(m => $"{m.Name} · {m.Hz:0.##} Hz · {m.Description}").ToList();
        TvScreenBox.SelectedIndex = screens.FindIndex(m => m.Name == tv);
        TvAuto.IsChecked = AppSettings.Current.AutoTvProfile;
        BtSwitch.IsEnabled = bt.Exists;
        BtSwitch.IsChecked = bt.Enabled;
        BtNote.Text = bt.Exists ? (bt.Active ? "" : "O serviço está parado.") : "Serviço xbox-bt-autoconnect não instalado.";
        string mango = Sys.Read(MangoHudConfig.FilePath) ?? "";
        MangoHidden.IsChecked = ConsoleSetup.MangoHidden(mango);
        MangoLog.IsChecked = ConsoleSetup.PharosLog(mango);
        MangoLog.IsEnabled = !MangoHudConfig.IsOurs; // o arquivo do overlay do PHAROS já traz o control=
        MangoNote.Text = $"Mostrar e esconder: {ConsoleSetup.MangoToggleKeys(mango)}, ou View + Start no controle. "
                         + (MangoHudConfig.IsOurs ? "O arquivo é o do overlay do PHAROS (página Overlay)." : "O PHAROS só muda esta linha do seu MangoHud.conf.");
        _loading = false;
    }

    private async Task TvStatus(bool hasTv)
    {
        if (!hasTv) { TvState.Text = "—"; return; }
        var r = await Sys.RunAsync(ConsoleSetup.Bin("tv-power"), "status");
        TvState.Text = r.Out.Trim() switch { "on" => "ligada", "off" => "desligada", _ => "sem resposta" };
    }

    private async void Toggle(Action change, TextBlock note, string ok)
    {
        if (_loading) return;
        string msg;
        try { change(); msg = ok; }
        catch (Exception ex) { msg = "Não consegui gravar: " + ex.Message; }
        await Refresh();
        note.Text = msg; // depois do Refresh, que reescreve as notas
    }

    private void SetMangoHidden()
    {
        bool hidden = MangoHidden.IsChecked == true;
        var s = AppSettings.Current;
        if (MangoHudConfig.IsOurs)
        {
            s.OverlayVisible = !hidden; // o arquivo é o do overlay do PHAROS: a opção dele manda
            s.Save();
            MangoHudConfig.Write(s);
        }
        else ConsoleSetup.EditFile(MangoHudConfig.FilePath, t => ConsoleSetup.SetMangoHidden(t, hidden));
        FrameLimiter.RewriteAll(s); // as configs por jogo do limitador copiam o MangoHud.conf
    }

    private async void SavePad_Click(object? sender, RoutedEventArgs e)
    {
        var d = ConsoleSetup.LoadButtons();
        foreach (var g in ConsoleSetup.Gestures) d[g.Key] = ConsoleSetup.Actions[Math.Max(0, _gesture[g.Key].SelectedIndex)].Id;
        d["GUIDE_VOLUME"] = VolSwitch.IsChecked == true ? "1" : "0";
        d["HOLD_SECONDS"] = HoldSlider.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            PadNote.Text = await ConsoleSetup.SaveButtons(d) is { } err ? "Gravei, mas o serviço não reiniciou: " + err : "Salvo. O controle já usa os atalhos novos.";
        }
        catch (Exception ex) { PadNote.Text = "Não consegui gravar: " + ex.Message; }
        await Refresh();
    }

    private async void RestartPad_Click(object? sender, RoutedEventArgs e)
    {
        PadNote.Text = await ConsoleSetup.RestartService(ConsoleSetup.ControllerService) ?? "Serviço reiniciado.";
        await Refresh();
    }

    private async void Tv_Click(object? sender, RoutedEventArgs e)
    {
        string cmd = (string)((Control)sender!).Tag!;
        TvState.Text = "…";
        var r = await Sys.RunAsync(ConsoleSetup.Bin("tv-power"), cmd);
        if (!r.Ok) TvText.Text = "A TV não respondeu: " + Sys.RootError(r);
        await TvStatus(true);
    }

    /// <summary>Fora do cgroup do PHAROS (systemd-run), como o daemon do controle faz: fechar o PHAROS não fecha o Hydra.</summary>
    private static Task<Sys.Result> Detached(params string[] argv) =>
        Sys.RunAsync("systemd-run", new[] { "--user", "--collect", "--quiet" }.Concat(argv));

    private async void OpenBp_Click(object? sender, RoutedEventArgs e) => await Detached(ConsoleSetup.HydraWrapper, "--big-picture");

    private async void OpenHydra_Click(object? sender, RoutedEventArgs e) => await Detached(ConsoleSetup.HydraWrapper);

    private async void SteamRestart_Click(object? sender, RoutedEventArgs e)
    {
        if (!_steamConfirm)
        {
            _steamConfirm = true;
            BtnSteamRestart.Content = "Confirmar: fecha a Steam e os jogos dela";
            return;
        }
        _steamConfirm = false;
        BtnSteamRestart.Content = "Reiniciar a Steam";
        BtnSteamRestart.IsEnabled = false;
        SteamNote.Text = "Fechando a Steam…";
        await Sys.RunAsync("steam", "-shutdown");
        for (int i = 0; i < 60 && (await Task.Run(ConsoleSetup.Steam)).Running; i++) await Task.Delay(500);
        SteamNote.Text = "Abrindo a Steam…";
        if (SteamMango.IsChecked == true) await Detached("env", "MANGOHUD=1", "steam", "-silent");
        else await Detached("steam", "-silent");
        await Task.Delay(3000);
        BtnSteamRestart.IsEnabled = true;
        await Refresh();
        SteamNote.Text = "Steam reiniciada.";
    }

    private async void Doctor_Click(object? sender, RoutedEventArgs e) => await RunDoctor();

    private void Tv_Mode_Click(object? sender, RoutedEventArgs e) => new TvWindow().Show();

    /// <summary>gaming-doctor em cartões por seção, com ✓ / ! / ✗ viram ícones.</summary>
    private async Task RunDoctor()
    {
        string bin = ConsoleSetup.Bin("gaming-doctor");
        Doctor.Children.Clear();
        if (!File.Exists(bin))
        {
            HeroTitle.Text = "PC como console";
            Doctor.Children.Add(new TextBlock { Text = "gaming-doctor não instalado (dotfiles, gaming/bin).", Classes = { "dim" } });
            return;
        }
        HeroTitle.Text = "Verificando…";
        var r = await Sys.RunAsync(bin, Array.Empty<string>(), env: new Dictionary<string, string> { ["LC_ALL"] = "C.UTF-8" });
        var checks = ConsoleSetup.ParseDoctor(r.Out);
        int bad = checks.Count(c => c.Mark == '✗'), warn = checks.Count(c => c.Mark == '!');
        HeroTitle.Text = bad > 0 ? $"{bad} problema(s) no console" : warn > 0 ? $"Pronto para jogar, com {warn} aviso(s)" : "Tudo pronto para jogar";
        HeroText.Text = bad + warn > 0 ? "Veja o diagnóstico no fim da página." : "O controle liga o PC e a TV, o botão Xbox abre o Hydra e os jogos rodam com MangoHud.";
        HeroIcon.Foreground = App.Brush(bad > 0 ? "Bad" : warn > 0 ? "Warn" : "Ok");
        foreach (var sec in checks.GroupBy(c => c.Section))
        {
            var col = new StackPanel { Width = 330, Margin = new(0, 0, 18, 14), Spacing = 5 };
            col.Children.Add(new TextBlock { Text = sec.Key.ToUpperInvariant(), Classes = { "label" }, Margin = new(0, 0, 0, 2) });
            foreach (var c in sec)
            {
                var (icon, brush) = c.Mark switch { '✓' => ("check-circle", "Ok"), '!' => ("alert-circle", "Warn"), _ => ("close-circle", "Bad") };
                var icn = MainWindow.Icon(icon, 15);
                icn.Foreground = App.Brush(brush);
                icn.VerticalAlignment = VerticalAlignment.Top;
                icn.Margin = new(0, 1, 0, 0);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(icn);
                row.Children.Add(new TextBlock { Text = c.Text, Classes = { "dim" }, FontSize = 12.5, MaxWidth = 300, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                col.Children.Add(row);
            }
            Doctor.Children.Add(col);
        }
    }
}
