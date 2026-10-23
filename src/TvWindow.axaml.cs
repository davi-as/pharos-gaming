using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PharosGaming;

/// <summary>
/// Modo TV: o essencial do jogo aberto em letras grandes (FPS, ritmo, limite, temperaturas), navegável pelo controle
/// ou pelas setas. Abre pela página PC como console, pelo controle (ação "Pharos na TV") ou com pharos-gaming --tv.
/// </summary>
public partial class TvWindow : Window
{
    private readonly Gamepad _pad;
    private RunningGame? _game;
    private FrameLimiter.Screen? _screen;
    private int _ticks;
    private bool _scanning;
    private readonly (TextBlock Value, TextBlock Detail)[] _hw;

    public TvWindow()
    {
        InitializeComponent();
        _hw = new[] { "GPU", "CPU", "MEMÓRIA", "TELA" }.Select(AddTile).ToArray();
        // o controle só manda aqui com a janela em foco: com o jogo na frente, os botões são do jogo
        _pad = new Gamepad(nav => Dispatcher.UIThread.Post(() => { if (IsActive) Navigate(nav); }));
        KeyDown += OnKey;
        Opened += (_, _) => { ShowLimits(); BtnBp.Focus(); };
        Closed += (_, _) => { _pad.Dispose(); Live.Tick -= Tick; };
        Live.Start();
        Live.Tick += Tick;
        Tick();
    }

    private (TextBlock, TextBlock) AddTile(string label)
    {
        var v = new TextBlock { Classes = { "tvbig" }, FontSize = 44, Text = "—" };
        var d = new TextBlock { FontSize = 20, Foreground = App.Brush("Muted"), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var p = new StackPanel { Margin = new(0, 0, 24, 0) };
        p.Children.Add(new TextBlock { Classes = { "tvlabel" }, Text = label });
        p.Children.Add(v);
        p.Children.Add(d);
        Hw.Children.Add(new Border { Classes = { "card" }, Margin = new(0, 0, 18, 0), Padding = new(26, 22), Child = p });
        return (v, d);
    }

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
            bool changed = g?.Key != _game?.Key;
            _game = g;
            _screen = sc;
            if (changed) ShowLimits();
        }
        var f = _game != null ? Live.Frames : null;
        Game.Text = _game?.Name ?? "Nenhum jogo aberto";
        Fps.Text = f != null ? $"{f.Fps:0}" : "—";
        Ft.Text = f != null ? $"{f.FrameTimeMs:0.0} ms por quadro · mínimo {f.MinFps:0}" : "";
        var v = f != null && _game != null ? FrameLimiter.Judge(Live.FrameTimes, Limit()) : null;
        Verdict.Text = _game == null ? "Abra um jogo pelo Hydra: ele aparece aqui sozinho."
                     : v != null ? $"{v.Title}. {v.Detail}" : f == null ? "Esperando o FPS do MangoHud…" : "Medindo o ritmo dos quadros…";
        Verdict.Foreground = App.Brush(v == null ? "Muted" : v.Good ? "Ok" : "Warn");

        var gpu = Live.Gpu;
        var cpu = Live.Cpu;
        var mem = Live.Mem;
        _hw[0].Value.Text = gpu?.Temp is float gt ? $"{gt:0} °C" : "—";
        _hw[0].Detail.Text = gpu?.Load is float gl ? $"{gl:0}% de uso" + (gpu.PowerW is float w ? $" · {w:0} W" : "") : "";
        _hw[1].Value.Text = cpu?.Temp is float ct ? $"{ct:0} °C" : "—";
        _hw[1].Detail.Text = cpu?.Total is float cl ? $"{cl:0}% de uso" : "";
        _hw[2].Value.Text = mem != null ? $"{mem.UsedGb:0.#} GB" : "—";
        _hw[2].Detail.Text = mem != null ? $"de {mem.TotalGb:0} GB" : "";
        _hw[3].Value.Text = _screen != null ? $"{_screen.Hz:0.#} Hz" : "—";
        _hw[3].Detail.Text = _screen != null ? _screen.Name + (_screen.Vrr ? " · VRR" : "") : "";
    }

    private double Limit() => _game != null && AppSettings.Current.FpsLimits.TryGetValue(_game.Key, out var l) ? l : 0;

    private void ShowLimits()
    {
        Limits.Children.Clear();
        LimitLabel.Text = _game == null ? "LIMITE DE FPS (abra um jogo)" : $"LIMITE DE FPS · {_game.Name}";
        var last = _game == null ? null : SessionLog.Load().Sessions.Where(s => s.Game == _game.Name).MaxBy(s => s.Start);
        LastSession.Text = last == null ? "" : $"Última sessão: {last.AvgFps:0} FPS médio, 1% low {last.Low1Fps:0}, ritmo ±{last.PacingMs:0.0} ms ({last.Start:dd/MM})";
        if (_game == null) return;
        var values = (_screen is { } sc ? FrameLimiter.Candidates(sc.Hz, sc.Vrr) : new() { 120, 60, 40, 30 }).Append(0);
        foreach (double v in values)
        {
            var b = new Button { Classes = { "tv" }, Content = v > 0 ? v.ToString("0.##") : "Sem limite" };
            if (Math.Abs(Limit() - v) < 0.01) b.Classes.Add("current");
            b.Click += (_, _) =>
            {
                try { FrameLimiter.Set(AppSettings.Current, _game!.Key, v); }
                catch (Exception) { return; } // sem permissão no ~/.config/MangoHud: o botão só não marca
                foreach (var o in Limits.Children.OfType<Button>()) o.Classes.Remove("current");
                b.Classes.Add("current");
            };
            Limits.Children.Add(b);
        }
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

    private async void Bp_Click(object? sender, RoutedEventArgs e) =>
        await Sys.RunAsync("systemd-run", "--user", "--collect", "--quiet", ConsoleSetup.HydraWrapper, "--big-picture");

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
