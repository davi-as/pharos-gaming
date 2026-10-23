using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PharosGaming;

/// <summary>
/// Menu rápido do Pharos na TV: a sessão do jogo aberto ("nunca abaixo de 60") e ajustes que valem com o jogo aberto
/// (limite, geração de quadros, overlay, energia), navegável pelo controle ou pelas setas. Abre com Guide + View (o
/// daemon do console põe a janela em tela cheia por cima do jogo e devolve o jogo quando ela fecha), pela página
/// PC como console ou com pharos-gaming --tv.
/// </summary>
public partial class TvWindow : Window
{
    private readonly Gamepad _pad;
    private readonly AppSettings _s = AppSettings.Current;
    private RunningGame? _game;
    private FrameLimiter.Screen? _screen;
    private int _ticks;
    private bool _scanning;
    private string? _power;
    private readonly Dictionary<string, (Button Button, TextBlock Value, TextBlock Hint)> _tile = new();
    private readonly List<(TextBlock Label, TextBlock Value)> _stats = new();

    public TvWindow()
    {
        InitializeComponent();
        string[] names = { "limit", "fg", "overlay", "power", "shot", "bp" };
        string[] labels = { "LIMITE", "GERAÇÃO DE QUADROS", "OVERLAY", "ENERGIA", "CAPTURA", "HYDRA" };
        for (int i = 0; i < names.Length; i++) AddTile(names[i], labels[i], i / 3, i % 3);
        for (int r = 0; r < 5; r++) AddStat(r);
        // o controle só manda aqui com a janela em foco: com o jogo na frente, os botões são do jogo
        _pad = new Gamepad(nav => Dispatcher.UIThread.Post(() => { if (IsActive) Navigate(nav); }));
        KeyDown += OnKey;
        Opened += (_, _) => _tile["limit"].Button.Focus();
        Closed += (_, _) => { _pad.Dispose(); Live.Tick -= Tick; };
        Live.Start();
        Live.Tick += Tick;
        _ = ReadPower();
        Tick();
    }

    private void AddTile(string name, string label, int row, int col)
    {
        var v = new TextBlock { Classes = { "tvbig" }, FontSize = 26, Text = "—" };
        var h = new TextBlock { Classes = { "tv" }, FontSize = 16, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Classes = { "tvlabel" }, FontSize = 13, Text = label });
        panel.Children.Add(v);
        panel.Children.Add(h);
        var b = new Button { Classes = { "tile" }, Content = panel, MinHeight = 140 };
        b.Click += (_, _) => Activate(name);
        Grid.SetRow(b, row);
        Grid.SetColumn(b, col);
        Tiles.Children.Add(b);
        _tile[name] = (b, v, h);
    }

    private void AddStat(int row)
    {
        var l = new TextBlock { Classes = { "tv" } };
        var v = new TextBlock { Classes = { "tv" }, Foreground = App.Brush("Fg"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        Stats.Children.Add(l);
        Stats.Children.Add(v);
        _stats.Add((l, v));
    }

    private void Stat(int i, string label, string value) => (_stats[i].Label.Text, _stats[i].Value.Text) = (label, value);

    private async void Tick()
    {
        Clock.Text = DateTime.Now.ToString("HH:mm");
        if (_ticks++ % 4 == 0 && !_scanning) // jogo e tela a cada 2 s
        {
            _scanning = true;
            var (g, sc) = await Task.Run(() =>
            {
                var game = FrameLimiter.Pick(FrameLimiter.Running(), FrameLimiter.WindowPids());
                return (game, FrameLimiter.ScreenOf(game?.Pid ?? -1)); // sem jogo: a tela em foco
            });
            _scanning = false;
            _game = g;
            _screen = sc;
        }
        ShowSession();
        ShowTiles();
    }

    private void ShowSession()
    {
        var f = _game != null ? Live.Frames : null;
        Game.Text = _game?.Name ?? "Nenhum jogo aberto";
        Fps.Text = f != null ? $"{f.Fps:0}" : "—";
        int mult = _game != null ? Lsfg.Multiplier(_s, _game.Key) : 1;
        Ft.Text = f == null ? "" : mult >= 2 ? $"FPS reais · {f.Fps * mult:0} na tela ({mult}×)" : $"FPS · {f.FrameTimeMs:0.0} ms";

        var floor = f != null ? Live.SessionFloor : null;
        double above = floor != null ? 100 - floor.BelowPct : 0;
        FloorBar.Width = Math.Max(0, (FloorBar.Parent as Control)?.Bounds.Width * above / 100 ?? 0);
        FloorBar.Background = App.Brush(above >= 99.5 ? "Ok" : "Warn");
        var start = Live.SessionStart;
        Stat(0, "Acima de 60", floor == null ? "—" : $"{above:0.#}% do tempo" + (start is DateTime t ? $" · {(DateTime.Now - t).TotalMinutes:0} min" : ""));
        Stat(1, "Quedas", floor == null ? "—" : floor.Drops == 0 ? "nenhuma" : $"{floor.Drops}, a pior em {floor.WorstFps:0}");
        var v = f != null ? FrameLimiter.Judge(Live.FrameTimes, Limit()) : null;
        Stat(2, "Ritmo dos quadros", v == null ? "—" : v.Title);
        Stat(3, "GPU · CPU", $"{(Live.Gpu?.Temp is float gt ? $"{gt:0} °C" : "—")} · {(Live.Cpu?.Temp is float ct ? $"{ct:0} °C" : "—")}");
        var last = _game == null ? null : SessionLog.Load().Sessions.Where(x => x.Game == _game.Name).MaxBy(x => x.Start);
        Stat(4, "Sessão anterior", last?.BelowPct is double b ? $"{100 - b:0.#}% acima de 60" : last != null ? $"{last.AvgFps:0} FPS médio" : "—");

        var tip = last != null ? SessionLog.Advice(last) : null;
        TipBox.IsVisible = _game != null;
        Tip.Text = _game == null ? "" : floor != null && floor.BelowPct >= 0.5
            ? $"Abaixo de 60 em {floor.BelowPct:0.#}% do tempo até agora. " + (tip != null && !tip.Good ? tip.Text : "Se continuar, baixe um degrau nos gráficos ou ligue upscaling (FSR, XeSS ou DLSS) em Qualidade.")
            : tip != null ? $"{tip.Title}. {tip.Text}" : "Segurando 60. A sugestão do jogo aparece aqui quando houver o que ajustar.";
        TipBox.BorderBrush = App.Brush(floor != null && floor.BelowPct >= 0.5 || tip is { Good: false } ? "Warn" : "Ok");
    }

    private double Limit() => _game != null && _s.FpsLimits.TryGetValue(_game.Key, out var l) ? l : 0;
    private double ScreenHz => _screen?.Hz ?? 60;

    private void ShowTiles()
    {
        bool has = _game != null;
        void T(string n, string value, string hint, bool enabled = true)
        {
            var t = _tile[n];
            t.Value.Text = value;
            t.Hint.Text = hint;
            t.Button.IsEnabled = enabled;
        }
        double lim = Limit();
        int mult = has ? Lsfg.Multiplier(_s, _game!.Key) : 1;
        T("limit", !has ? "—" : lim > 0 ? $"{lim:0.##}" : "Sem limite",
          !has ? "abra um jogo" : Math.Abs(lim * mult - ScreenHz) < 0.6 ? "igual à TV" : "A troca o valor", has);
        string? fgMissing = Lsfg.Missing();
        T("fg", !has ? "—" : mult >= 2 ? $"{mult}× ligada" : "Desligada",
          fgMissing ?? (mult >= 2 ? $"{ScreenHz / mult:0.##} reais × {mult}" : "2× = metade reais, mais atraso"), has && fgMissing == null);
        bool limited = has && _s.FpsLimits.ContainsKey(_game!.Key);
        bool hidden = has && (_s.OverlayHidden.TryGetValue(_game!.Key, out bool h) ? h : ConsoleSetup.MangoHidden(Sys.Read(MangoHudConfig.FilePath) ?? ""));
        T("overlay", !has ? "—" : hidden ? "Escondido" : "Visível", has && !limited ? "precisa de um limite salvo" : "o FPS chega ao Pharos igual", limited);
        T("power", _power == "performance" ? "Desempenho" : _power == null ? "—" : "Equilibrado",
          _power == null ? "sem power-profiles-daemon" : "CPU sem economia", _power != null);
        T("shot", "Tirar foto", "fecha o menu e captura o jogo");
        T("bp", "Big Picture", "o jogo continua aberto");
    }

    private async Task ReadPower()
    {
        _power = await Task.Run(BatteryInfo.PowerProfile);
        ShowTiles();
    }

    private async void Activate(string name)
    {
        var g = _game;
        try
        {
            switch (name)
            {
                case "limit" when g != null:
                    // os valores que casam com a tela, e sem limite; com geração ligada, o limite é dos quadros reais
                    int mult = Lsfg.Multiplier(_s, g.Key);
                    var values = FrameLimiter.Candidates(ScreenHz, _screen?.Vrr == true).Select(v => Math.Round(v / mult, 2)).Append(0).Distinct().ToList();
                    int i = values.FindIndex(v => Math.Abs(v - Limit()) < 0.01);
                    FrameLimiter.Set(_s, g.Key, values[(i + 1) % values.Count]);
                    break;
                case "fg" when g != null:
                    Lsfg.Set(_s, g.Key, Lsfg.Multiplier(_s, g.Key) >= 2 ? 1 : 2, ScreenHz);
                    break;
                case "overlay" when g != null:
                    bool hidden = _s.OverlayHidden.TryGetValue(g.Key, out bool h) ? h : ConsoleSetup.MangoHidden(Sys.Read(MangoHudConfig.FilePath) ?? "");
                    _s.OverlayHidden[g.Key] = !hidden;
                    _s.Save();
                    FrameLimiter.Write(_s, g.Key); // o MangoHud relê; o log volta pelo control= novo
                    break;
                case "power" when _power != null:
                    await Sys.RunAsync("powerprofilesctl", "set", _power == "performance" ? "balanced" : "performance");
                    await ReadPower();
                    break;
                case "shot":
                    // depois que o menu fecha e o jogo volta à frente
                    await Sys.RunAsync("systemd-run", "--user", "--collect", "--quiet", "sh", "-c", $"sleep 1; exec {Sys.Quote(ConsoleSetup.Bin("controller-screenshot"))}");
                    Close();
                    return;
                case "bp":
                    await Sys.RunAsync("systemd-run", "--user", "--collect", "--quiet", ConsoleSetup.HydraWrapper, "--big-picture");
                    break;
            }
        }
        catch (Exception ex) { Tip.Text = "Não consegui mudar: " + ex.Message; }
        ShowTiles();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        PadNav? nav = e.Key switch
        {
            Key.Up => PadNav.Up, Key.Down => PadNav.Down, Key.Left => PadNav.Left, Key.Right => PadNav.Right,
            Key.Enter or Key.Space => PadNav.Confirm, Key.Escape or Key.Back => PadNav.Back, _ => null,
        };
        if (nav == null) return;
        e.Handled = true;
        Navigate(nav.Value);
    }

    /// <summary>Navegação espacial: o botão mais próximo na direção pedida, pela posição na tela.</summary>
    private void Navigate(PadNav nav)
    {
        if (nav == PadNav.Back) { Close(); return; }
        var buttons = this.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled).ToList();
        var cur = buttons.FirstOrDefault(b => b.IsFocused);
        if (nav == PadNav.Confirm) { cur?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }
        if (cur == null) { buttons.FirstOrDefault()?.Focus(); return; }
        Point Center(Button b) => b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), this) ?? default;
        var c = Center(cur);
        var next = buttons.Where(b => b != cur).Select(b => (b, d: Center(b) - c))
            .Where(x => nav switch
            {
                PadNav.Up => x.d.Y < -10, PadNav.Down => x.d.Y > 10, PadNav.Left => x.d.X < -10, _ => x.d.X > 10,
            })
            // fora do eixo pesa mais: andar para a direita prefere o vizinho da mesma linha
            .OrderBy(x => nav is PadNav.Up or PadNav.Down ? Math.Abs(x.d.Y) + 3 * Math.Abs(x.d.X) : Math.Abs(x.d.X) + 3 * Math.Abs(x.d.Y))
            .Select(x => x.b).FirstOrDefault();
        next?.Focus(NavigationMethod.Directional);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
