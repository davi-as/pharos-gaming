using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Aba JOGOS: lista os jogos instalados, guarda um perfil por jogo e inicia aplicando o perfil.</summary>
public partial class LibraryPage : UserControl
{
    private readonly AppSettings _settings = AppSettings.Current;
    private List<LibGame> _libGames = new();
    private LibGame? _lg;
    private GameProfile? _lp;
    private bool _lLoading, _steamConfirm;
    private CancellationTokenSource? _launchCts;

    private static readonly (string Label, string Token, string? Hint)[] ExtraPresets =
    {
        ("Pular intro (-novid)", "-novid", null), ("Sem limite de FPS (+fps_max 0)", "+fps_max 0", null),
        ("Sem joystick (-nojoy)", "-nojoy", null), ("Abrir console (-console)", "-console", null),
        ("Programas de terceiros (-allow_third_party_software)", "-allow_third_party_software",
         "CS2: libera programas que o modo confiável bloqueia, como a captura de jogo do OBS, ReShade e alguns overlays.\n" +
         "Atenção: a Valve diz que usar esta opção pode reduzir o seu Fator de Confiança (Trust Factor) no matchmaking."),
    };

    public LibraryPage()
    {
        InitializeComponent();
        foreach (var (label, token, hint) in ExtraPresets)
        {
            var b = new Button { Content = label, FontSize = 11, Margin = new Thickness(0, 0, 6, 6) };
            if (hint != null) ToolTip.SetTip(b, hint);
            b.Click += (_, _) =>
            {
                if (!(LExtra.Text ?? "").Contains(token, StringComparison.OrdinalIgnoreCase))
                    LExtra.Text = ((LExtra.Text ?? "").Trim() + " " + token).Trim();
            };
            LExtraPresets.Children.Add(b);
        }
        var missing = new[] { "mangohud", "gamemoderun" }.Where(c => !GameLauncher.HasCommand(c)).ToList();
        LToolsNote.Text = missing.Count == 0
            ? "MangoHud e GameMode valem só enquanto o jogo roda. Nada é alterado no sistema."
            : $"Não instalado: {string.Join(", ", missing)} (pacotes mangohud e gamemode da sua distro). Sem eles, o jogo não abre com a opção marcada.";
        _ = ScanGamesAsync();
    }

    // ---------------- Lista ----------------

    private async Task ScanGamesAsync()
    {
        BtnScanGames.IsEnabled = false;
        LGameCount.Text = "Procurando seus jogos…";
        try
        {
            var custom = _settings.CustomGames.ToList();
            _libGames = await Task.Run(() => GameLibrary.Scan(custom));
            ApplyGameFilter();
            var byStore = _libGames.GroupBy(g => g.Store).Select(x => $"{x.Key} {x.Count()}");
            LGameCount.Text = _libGames.Count == 0
                ? "Nenhum jogo encontrado. Use + ADICIONAR JOGO para incluir um à mão."
                : $"{_libGames.Count} jogos · {string.Join(" · ", byStore)}";
        }
        catch (Exception ex) { LGameCount.Text = "Falhou: " + ex.Message; }
        finally { BtnScanGames.IsEnabled = true; }
    }

    private void ApplyGameFilter()
    {
        string q = (LGameSearch.Text ?? "").Trim();
        string? keep = _lg?.Key;
        var list = _libGames.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        LGameList.ItemsSource = list;
        if (keep != null) LGameList.SelectedItem = list.FirstOrDefault(g => g.Key == keep);
    }

    private void LGameSearch_Changed(object? sender, TextChangedEventArgs e) => ApplyGameFilter();

    private async void BtnScanGames_Click(object? sender, RoutedEventArgs e) => await ScanGamesAsync();

    private async Task<string?> PickFileAsync(string title, string? startDir = null)
    {
        var sp = TopLevel.GetTopLevel(this)!.StorageProvider;
        var opts = new FilePickerOpenOptions { Title = title };
        if (startDir != null && Directory.Exists(startDir)) opts.SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(startDir);
        var files = await sp.OpenFilePickerAsync(opts);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void BtnAddExe_Click(object? sender, RoutedEventArgs e)
    {
        string? file = await PickFileAsync("Escolha o executável ou script do jogo");
        if (file == null) return;
        if (!_settings.CustomGames.Any(c => c.Exe == file))
        {
            _settings.CustomGames.Add(new CustomGame { Name = Path.GetFileNameWithoutExtension(file), Exe = file });
            SaveSettings();
        }
        await ScanGamesAsync();
        LGameList.SelectedItem = _libGames.FirstOrDefault(g => g.Store == "Manual" && g.Id == file);
    }

    private void SaveSettings()
    {
        try { _settings.Save(); } catch { }
    }

    // ---------------- Perfil ----------------

    private static void SetChip(Panel panel, string tag)
    {
        var chips = panel.Children.OfType<RadioButton>().ToList();
        var hit = chips.FirstOrDefault(c => c.Tag as string == tag) ?? chips.FirstOrDefault();
        if (hit != null) hit.IsChecked = true;
    }

    private static string? CheckedTag(Panel panel) =>
        panel.Children.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag as string;

    private void LGameList_Changed(object? sender, SelectionChangedEventArgs e)
    {
        _lg = LGameList.SelectedItem as LibGame;
        _steamConfirm = false;
        BtnSteamApply.Content = "APLICAR NAS OPÇÕES DA STEAM";
        LDetail.IsVisible = _lg != null;
        LEmpty.IsVisible = _lg == null;
        if (_lg == null) { _lp = null; return; }

        var g = _lg;
        if (!_settings.GameProfiles.TryGetValue(g.Key, out var p)) { p = new GameProfile(); _settings.GameProfiles[g.Key] = p; }
        _lLoading = true;
        _lp = p;
        LTitle.Text = g.Name;
        LSub.Text = $"{g.Store} · {g.InstallDir}";
        LChkMango.IsChecked = p.MangoHud;
        LChkGameMode.IsChecked = p.GameMode;
        SetChip(LModePanel, p.WinMode);
        SetChip(LResPanel, p.Res);
        SetChip(LHzPanel, p.Hz.ToString());
        SetChip(LStylePanel, p.Style);
        LExtra.Text = p.Extra;
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = p.Helpers;
        LSteamBox.IsVisible = g.IsSteam;
        ShowSteamNow(g);
        LGfxNote.Text = g.IsSteam
            ? "A linha só passa a valer na Steam depois de APLICAR NAS OPÇÕES DA STEAM (a Steam precisa ser fechada nessa hora). Depois, o JOGAR abre pela Steam normalmente."
            : "Ao clicar em JOGAR, o PHAROS roda esta linha com %command% trocado pelo executável do jogo.";
        LStatus.Text = "";
        _lLoading = false;
        UpdateLauncherPreview();
    }

    private void ShowSteamNow(LibGame g) =>
        LSteamNow.Text = g.IsSteam ? "Opções atuais na Steam: " + (SteamInfo.GetLaunchOptions(g.Id) is { Length: > 0 } cur ? cur : "(nenhuma)") : "";

    private void SaveProfileFromControls()
    {
        if (_lLoading || _lp == null) return;
        _lp.MangoHud = LChkMango.IsChecked == true;
        _lp.GameMode = LChkGameMode.IsChecked == true;
        _lp.WinMode = CheckedTag(LModePanel) ?? "default";
        _lp.Res = CheckedTag(LResPanel) ?? "default";
        _lp.Hz = int.TryParse(CheckedTag(LHzPanel), out int hz) ? hz : 0;
        _lp.Style = CheckedTag(LStylePanel) ?? "source";
        _lp.Extra = (LExtra.Text ?? "").Trim();
        SaveSettings();
        UpdateLauncherPreview();
    }

    // RadioButton desmarcado também dispara: só salva quando algo foi marcado (ou é um CheckBox)
    private void LProfile_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: not true }) return;
        SaveProfileFromControls();
    }

    private void LExtra_Changed(object? sender, TextChangedEventArgs e) => SaveProfileFromControls();

    private void UpdateLauncherPreview()
    {
        if (_lp == null) return;
        string a = GameLauncher.LaunchLine(_lp);
        LPreview.Text = a.Length > 0 ? a : "(nada)";
    }

    // ---------------- Programas auxiliares ----------------

    private async void BtnAddHelper_Click(object? sender, RoutedEventArgs e)
    {
        var p = _lp;
        if (p == null) return;
        string? file = await PickFileAsync("Programa para abrir junto com o jogo");
        if (file == null) return;
        p.Helpers.Add(new HelperProgram { Path = file, CloseWithGame = LHelperClose.IsChecked == true });
        SaveSettings();
        if (_lp != p) return;
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = p.Helpers;
    }

    private void BtnRemoveHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (_lp == null || (sender as Control)?.Tag is not HelperProgram h) return;
        _lp.Helpers.Remove(h);
        SaveSettings();
        LHelpers.ItemsSource = null;
        LHelpers.ItemsSource = _lp.Helpers;
    }

    // ---------------- Steam: opções de inicialização ----------------

    private async void BtnSteamApply_Click(object? sender, RoutedEventArgs e)
    {
        var g = _lg;
        if (g == null || !g.IsSteam || _lp == null) return;
        string opts = GameLauncher.LaunchLine(_lp);
        string cur = SteamInfo.GetLaunchOptions(g.Id) ?? "";
        bool replaces = cur.Length > 0 && cur != opts;
        if ((SteamInfo.IsRunning || replaces) && !_steamConfirm)
        {
            // sem caixa de diálogo no Avalonia: o segundo clique confirma
            _steamConfirm = true;
            BtnSteamApply.Content = SteamInfo.IsRunning ? "FECHAR A STEAM E GRAVAR" : "SUBSTITUIR E GRAVAR";
            LStatus.Text = (replaces ? $"Isto substitui as opções atuais do jogo ({cur}). Se quiser mantê-las, copie o que precisar para os Extras. " : "") +
                           (SteamInfo.IsRunning ? "A Steam precisa ser fechada para gravar (senão ela sobrescreve as opções ao sair). Jogos abertos serão encerrados. " : "") +
                           "Clique de novo para confirmar. Um backup do arquivo é feito antes.";
            return;
        }
        _steamConfirm = false;
        BtnSteamApply.Content = "APLICAR NAS OPÇÕES DA STEAM";
        BtnSteamApply.IsEnabled = false;
        try
        {
            if (SteamInfo.IsRunning)
            {
                LStatus.Text = "Fechando a Steam…";
                if (!await SteamInfo.ShutdownAsync()) { LStatus.Text = "A Steam não fechou. Feche-a manualmente e tente de novo."; return; }
            }
            string? err = await Task.Run(() => SteamInfo.SetLaunchOptions(g.Id, opts));
            LStatus.Text = err ?? (opts.Length > 0 ? $"Opções gravadas na Steam: {opts}. Clique em JOGAR (a Steam abre sozinha)." : "Opções da Steam limpas para este jogo.");
            if (_lg == g) ShowSteamNow(g);
        }
        finally { BtnSteamApply.IsEnabled = true; }
    }

    // ---------------- Jogar ----------------

    private async void BtnPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (_launchCts != null) { _launchCts.Cancel(); return; }
        var g = _lg;
        var p = _lp;
        if (g == null || p == null) return;

        _launchCts = new CancellationTokenSource();
        BtnPlay.Content = "PARAR ACOMPANHAMENTO";
        try
        {
            await GameLauncher.RunAsync(g, p, t => Dispatcher.UIThread.Post(() => LStatus.Text = t), _launchCts.Token);
        }
        finally
        {
            _launchCts.Dispose();
            _launchCts = null;
            BtnPlay.Content = "JOGAR";
        }
    }
}
