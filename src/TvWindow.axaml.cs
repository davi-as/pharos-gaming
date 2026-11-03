using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Animation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace PharosGaming;

/// <summary>
/// Menu rápido do Pharos na TV: a sessão do jogo aberto ("nunca abaixo de 60") e ajustes que valem com o jogo aberto
/// (limite, geração de quadros, overlay, energia), navegável pelo controle ou pelas setas. Pela página PC como console
/// abre como janela em tela cheia. Com pharos-gaming --tv é o overlay: o processo fica aberto, o Guide + View do daemon
/// do console manda SIGUSR1 e a janela aparece translúcida por cima do jogo, sem tirar o foco nem a tela cheia dele
/// (regra de janela do Hyprland), com o controle preso só para o menu; B ou Guide + View esconde de novo.
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
    private string? _sink;
    private int? _volume;
    private bool _screensOff, _padConfirm;
    private readonly Dictionary<string, (Button Button, TextBlock Value, TextBlock Hint, Border Icon)> _tile = new();
    private IBrush? _floorGradient;

    private static readonly Dictionary<string, string> Icons = new()
    {
        ["limit"] = "timer-outline", ["fg"] = "animation-outline", ["fglight"] = "feather", ["overlay"] = "eye-outline", ["vsync"] = "refresh",
        ["audio"] = "speaker", ["voldown"] = "volume-minus", ["volup"] = "volume-plus", ["power"] = "lightning-bolt-outline",
        ["screens"] = "monitor-multiple", ["pad"] = "bluetooth-off", ["shot"] = "camera-outline", ["bp"] = "play-circle-outline",
        ["theme"] = "theme-light-dark", ["scale"] = "magnify", ["motion"] = "animation-outline", ["reset"] = "restart",
    };
    private static readonly double[] Scales = { 0.85, 1.0, 1.15, 1.3 };
    public const string OverlayTitle = "Pharos Gaming · Overlay";
    private readonly bool _overlay;
    private bool _entered;
    private PosixSignalRegistration? _toggle;
    private readonly Dictionary<int, bool> _vsyncAtLaunch = new(); // pid do jogo → V-Sync rápido quando o Pharos o viu (o que vale até reabrir)
    private readonly List<(TextBlock Label, TextBlock Value)> _stats = new();

    public TvWindow() : this(false) { }

    public TvWindow(bool overlay)
    {
        _overlay = overlay;
        InitializeComponent();
        // seções: o que precisa do jogo aberto, o que é do PC e os atalhos (com a sugestão ao lado)
        (string, string)[] game = { ("limit", "LIMITE"), ("fg", "GERAÇÃO"), ("fglight", "QUALIDADE"), ("vsync", "V-SYNC"), ("overlay", "OVERLAY") };
        (string, string)[] system = { ("audio", "SOM"), ("power", "ENERGIA"), ("screens", "TELAS"), ("pad", "CONTROLE") };
        (string, string)[] shortcuts = { ("shot", "CAPTURA"), ("bp", "HYDRA") };
        (string, string)[] settings = { ("theme", "TEMA"), ("scale", "TAMANHO"), ("motion", "ANIMAÇÕES"), ("reset", "PADRÃO") };
        foreach (var (grid, items) in new[] { (GameTiles, game), (SystemTiles, system), (ShortcutTiles, shortcuts), (SettingsTiles, settings) })
            for (int i = 0; i < items.Length; i++) AddTile(items[i].Item1, items[i].Item2, grid, i);
        for (int r = 0; r < 4; r++) AddStat(PerfStats, r); // a sessão
        // janela comum: o controle só manda aqui com ela em foco (com o jogo na frente, os botões são do jogo);
        // overlay: nunca tem foco, manda enquanto aparece (o controle fica preso para o menu)
        _pad = new Gamepad(nav => Dispatcher.UIThread.Post(() => { if (_overlay ? IsVisible && _entered : IsActive) Navigate(nav); }));
        KeyDown += OnKey;
        _floorGradient = FloorBar.Background;
        ApplyLook();
        Opened += (_, _) => Enter();
        Closed += (_, _) => { _pad.Dispose(); Live.Tick -= Tick; _toggle?.Dispose(); };
        if (overlay)
        {
            Title = OverlayTitle;
            WindowState = WindowState.Normal; // tela cheia tiraria a do jogo: o tamanho vem da regra do Hyprland
            SystemDecorations = SystemDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            OverlayRule();
            const PosixSignal SigUsr1 = (PosixSignal)10; // o daemon do console: Guide + View
            _toggle = PosixSignalRegistration.Create(SigUsr1, c => { c.Cancel = true; Dispatcher.UIThread.Post(Toggle); });
        }
        Live.Start();
        Live.Tick += Tick;
        _ = ReadPower();
        _ = ReadAudio();
        Tick();
    }

    private IBrush Br(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    private Button? FirstEnabled() => _tile.Values.Select(t => t.Button).FirstOrDefault(b => b.IsEffectivelyEnabled);

    private void AddTile(string name, string label, Grid grid, int col)
    {
        bool compact = grid == GameTiles; // a seção do jogo tem 5 botões: um pouco menores para os textos caberem
        var icon = new Border { Classes = { "ico" }, Child = new PathIcon { Data = (Geometry)this.FindResource("ic-" + Icons[name])!, Width = compact ? 19 : 22, Height = compact ? 19 : 22 } };
        if (compact) (icon.Width, icon.Height, icon.CornerRadius) = (36, 36, new CornerRadius(18));
        var v = new TextBlock { Classes = { "serif" }, FontSize = compact ? 19 : 22, Text = "—", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 10, 0, 0) };
        var h = new TextBlock { Classes = { "soft" }, FontSize = compact ? 13.5 : 15, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 3, 0, 0) };
        var panel = new StackPanel();
        var top = new Grid { ColumnDefinitions = new("Auto,*") };
        top.Children.Add(icon);
        var cap = new TextBlock { Classes = { "cap" }, FontSize = 12, Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(compact ? 9 : 12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(cap, 1);
        top.Children.Add(cap);
        panel.Children.Add(top);
        panel.Children.Add(v);
        panel.Children.Add(h);
        var b = new Button { Classes = { "tile" }, Content = panel, Height = 140 };
        if (compact) b.Padding = new Thickness(15, 16);
        b.Click += (_, _) => Activate(name);
        Grid.SetColumn(b, col);
        grid.Children.Add(b);
        _tile[name] = (b, v, h, icon);
    }

    /// <summary>Entrada: o Φ sobe, as letras "haros" entram uma a uma (como no login do Pharos), os botões em cascata.</summary>
    private void Intro()
    {
        bool anim = _s.TvMotion;
        void Prepare(Control c, double dy)
        {
            c.Opacity = 0;
            if (!anim) return;
            c.RenderTransform = TransformOperations.Parse($"translateY({dy}px)");
            c.Transitions ??= new Transitions();
            c.Transitions.Add(new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(420), Easing = new Avalonia.Animation.Easings.CubicEaseOut() });
            c.Transitions.Add(new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(520), Easing = new Avalonia.Animation.Easings.CubicEaseOut() });
        }
        void Show(Control c, int ms) => DispatcherTimer.RunOnce(() => { c.Opacity = 1; c.RenderTransform = TransformOperations.Parse("translateY(0px)"); }, TimeSpan.FromMilliseconds(anim ? ms : 1));

        Word.Children.Clear();
        var letters = "haros".Select(ch => new TextBlock { Classes = { "ink" }, Text = ch.ToString(), FontSize = 50, FontWeight = FontWeight.Medium, Margin = new(0, 0, -0.5, -6) }).ToList();
        foreach (var l in letters) Word.Children.Add(l);
        if (_overlay)
        {
            // overlay: um esmaecer só, rápido, do menu inteiro (como o da Steam); a cascata da marca é da janela comum
            Phi.Opacity = 1;
            Zoom.Transitions = anim ? new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new Avalonia.Animation.Easings.CubicEaseOut() } } : null;
            Zoom.Opacity = 1;
            return;
        }
        Prepare(Zoom, 0); // no Zoom, não no Root: o RenderTransform do Root é a escala do tamanho
        Prepare(Phi, 18);
        foreach (var l in letters) Prepare(l, 12);
        var tiles = _tile.Values.Select(t => t.Button).ToList();
        if (anim) foreach (var t in tiles) t.Opacity = 0; // a transição de opacidade vem do estilo do botão
        Prepare(SessionCard, 24);
        Show(Zoom, 10);
        Show(Phi, 60);
        for (int i = 0; i < letters.Count; i++) Show(letters[i], 380 + 70 * i);
        Show(SessionCard, 160);
        // no fim da entrada a opacidade volta ao estilo: 1, ou 0,42 nos que precisam do jogo aberto (tons mais fracos)
        for (int i = 0; i < tiles.Count; i++) { var t = tiles[i]; DispatcherTimer.RunOnce(() => t.ClearValue(OpacityProperty), TimeSpan.FromMilliseconds(220 + 30 * i)); }
    }

    private void AddStat(Grid stats, int row)
    {
        var gap = new Thickness(0, 3);
        var l = new TextBlock { Classes = { "soft" }, FontSize = 19, Margin = gap };
        var v = new TextBlock { Classes = { "ink" }, FontSize = 19, FontWeight = FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Right, Margin = gap };
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        stats.Children.Add(l);
        stats.Children.Add(v);
        _stats.Add((l, v));
    }

    private void Stat(int i, string label, string value) => (_stats[i].Label.Text, _stats[i].Value.Text) = (label, value);

    private async void Tick()
    {
        bool shown = IsVisible; // overlay escondido: só acha o jogo e mede o gargalo (nada de ddcutil, bateria, som)
        if (shown) Clock.Text = DateTime.Now.ToString("HH:mm");
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
            if (g != null) _vsyncAtLaunch.TryAdd(g.Pid, _s.FastVsync.Contains(g.Key));
            if (!IsVisible) return;
            _screensOff = await Task.Run(() => QuickActions.OthersOff(sc?.Name));
            var bats = await Task.Run(() => QuickActions.PadBatteries());
            BatteryRow.IsVisible = bats.Count > 0;
            Battery.Text = string.Join(" · ", bats.Select(QuickActions.Text));
            Battery.Foreground = Br(bats.Any(QuickActions.Low) ? "TvBad" : "TvSoft");
        }
        if (!IsVisible) return;
        if (_ticks % 8 == 1) _ = ReadAudio(); // alguém pode mudar o som por fora
        ShowSession();
        ShowTiles();
    }

    private void ShowSession()
    {
        var f = _game != null ? Live.Frames : null;
        Game.Text = _game?.Name ?? "Nenhum jogo aberto";
        Fps.Text = f != null ? $"{f.Fps:0}" : "—";
        int mult = _game != null ? _s.FgApplied.GetValueOrDefault(_game.Key, 1) : 1; // a que está valendo agora
        Ft.Text = f == null ? "" : mult >= 2 ? $"reais · {f.Fps * mult:0} na tela ({mult}×)" : $"{f.FrameTimeMs:0.0} ms por quadro";

        var floor = f != null ? Live.SessionFloor : null;
        double above = floor != null ? 100 - floor.BelowPct : 0;
        FloorBar.Width = Math.Max(0, (FloorBar.Parent as Control)?.Bounds.Width * above / 100 ?? 0);
        FloorBar.Background = above >= 99.5 || floor == null ? _floorGradient : Br("TvWarn");
        FloorPct.Text = floor == null ? "" : $"{above:0.#}%";
        var start = Live.SessionStart;
        Stat(0, "Tempo jogando", start is DateTime t && floor != null ? $"{(DateTime.Now - t).TotalMinutes:0} min" : "—");
        Stat(1, "Quedas", floor == null ? "—" : floor.Drops == 0 ? "nenhuma" : $"{floor.Drops}, a pior em {floor.WorstFps:0}");
        var v = f != null ? FrameLimiter.Judge(Live.FrameTimes, Limit()) : null;
        Stat(2, "Ritmo dos quadros", v == null ? "—" : v.Title);
        var bn = _game != null ? Live.Bottleneck : null; // medido no Live (o mesmo que a página de gargalo do app)
        var main = bn?.Distribution().FirstOrDefault();
        ShowBottleneck(main, f != null);
        var last = _game == null ? null : SessionLog.Load().Sessions.Where(x => x.Game == _game.Name).MaxBy(x => x.Start);
        double lim = Limit();
        int want = _game != null ? Lsfg.Multiplier(_s, _game.Key) : 1;
        bool hiddenNow = _game != null && (_s.OverlayHidden.TryGetValue(_game.Key, out bool oh) ? oh : ConsoleSetup.MangoHidden(Sys.Read(MangoHudConfig.FilePath) ?? ""));
        // limite · geração · overlay numa linha (os botões ao lado mudam cada um)
        ConfigLine.Text = _game == null ? "—" : string.Join("  ·  ",
            lim > 0 ? $"limite {lim:0.##} FPS" : "sem limite",
            want >= 2 ? $"geração {want}×" + (mult != want ? " ao reabrir" : "") + (_s.FgLight.Contains(_game.Key) ? " leve" : "") : mult >= 2 ? "geração desliga ao reabrir" : "sem geração",
            hiddenNow ? "FPS escondido" : "FPS visível") + (_s.FastVsync.Contains(_game.Key) ? "  ·  V-Sync rápido" : "");
        Stat(3, "Sessão anterior", last?.BelowPct is double b ? $"{100 - b:0.#}% acima" + (last.MainLimiter is Limiter pl ? $" · {Who(pl)}" : " de 60")
                                   : last != null ? $"{last.AvgFps:0} FPS médio" : "—");

        var tip = last != null ? SessionLog.Advice(last) : null;
        TipBox.IsVisible = _game != null;
        Tip.Text = _game == null ? "" : floor != null && floor.BelowPct >= 0.5
            ? $"Abaixo de 60 em {floor.BelowPct:0.#}% do tempo até agora" + (main is { } why
                ? $". Motivo: {BottleneckAnalyzer.Short(why.Limiter)}"
                : ". " + (tip != null && !tip.Good ? tip.Text : "Se continuar, baixe um degrau nos gráficos ou ligue upscaling (FSR, XeSS ou DLSS) em Qualidade."))
            : tip != null ? $"{tip.Title}. {tip.Text}" : "Segurando 60. A sugestão do jogo aparece aqui quando houver o que ajustar.";
        // calor: a GPU segurando o clock em boa parte da sessão vem antes de tudo (derruba a folga de qualquer ajuste)
        double hot = bn is { Count: >= 30 } ? 100.0 * bn.Hot / bn.Count : 0;
        if (_game != null && hot >= 20)
            Tip.Text = $"GPU quente: perdeu clock por temperatura em {hot:0}% da sessão{(Live.Gpu?.Temp is float ht ? $", agora a {ht:0} °C" : "")}. Confira ventilação e poeira. " + Tip.Text;
        bool warn = floor != null && floor.BelowPct >= 0.5 || tip is { Good: false } || hot >= 20;
        TipBox.BorderBrush = Br(warn ? "TvWarn" : "TvEdge");
        TipIcon.Data = (Geometry)this.FindResource(warn ? "ic-alert-circle" : "ic-check-circle")!;
        TipIcon.Foreground = Br(warn ? "TvWarn" : "TvOk");
        DrawGraph(f != null ? Live.RecentFrameTimes : Array.Empty<double>());
    }

    /// <summary>
    /// Bloco do gargalo: quem mais segurou o FPS na sessão e, agora, o uso da GPU e da thread mais ocupada do jogo (a folga
    /// de cada uma: a CPU total parece sobrar quando o jogo depende de uma thread só). Temperaturas ao lado.
    /// </summary>
    private void ShowBottleneck((Limiter Limiter, double Pct)? main, bool live)
    {
        LimTitle.Text = main is { } m ? BottleneckAnalyzer.Title(m.Limiter) : live ? "medindo…" : "—";
        LimPct.Text = main is { } p ? $"{p.Pct:0}% da sessão" : "";
        LimTitle.Foreground = Br(main?.Limiter is Limiter.Gpu or Limiter.Capped or null ? "TvInk" : "TvWarn");
        float? gpu = live ? Live.Gpu?.Load : null, thread = live ? Live.GameThread : null;
        string Temp(float? t) => t is float v ? $" · {v:0} °C" : "";
        GpuText.Text = gpu is float g ? $"{g:0}%{Temp(Live.Gpu?.Temp)}" : "—";
        GpuText.Foreground = Br(live && Live.Gpu?.ThermalCap == true ? "TvWarn" : "TvInk"); // segurando o clock por temperatura
        ThreadText.Text = thread is float th ? $"{Math.Min(th, 100):0}%" + (Live.Cpu?.Temp is float ct ? $" · CPU {ct:0} °C" : "") : "—";
        foreach (var (bar, v) in new[] { (GpuBar, gpu), (ThreadBar, thread) })
        {
            bar.Width = Math.Max(0, (bar.Parent as Control)?.Bounds.Width * Math.Clamp(v ?? 0, 0, 100) / 100 ?? 0);
            bar.Background = Br(v >= 80 ? "TvWarn" : "TvLapis"); // perto do limite acende em âmbar
        }
    }

    /// <summary>Tempo de cada quadro no último minuto: linha reta = liso; picos = engasgos. A linha fina é o alvo (16,7 ms).</summary>
    private void DrawGraph(IReadOnlyList<double> ft)
    {
        Graph.Children.Clear();
        double w = Graph.Bounds.Width, h = Graph.Bounds.Height;
        GraphNote.Text = ft.Count > 1 ? $"máx. {ft.Max():0.#} ms" : "";
        if (ft.Count < 2 || w <= 0 || h <= 0) return;
        double target = Limit() > 0 ? 1000 / Limit() : 1000 / 60.0;
        double top = Math.Max(target * 2.2, ft.Max() * 1.1);
        double Y(double v) => h - Math.Min(v, top) / top * h;
        Graph.Children.Add(new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(0, Y(target)), EndPoint = new Point(w, Y(target)), Stroke = Br("TvEdge"), StrokeThickness = 1.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
        });
        var pts = ft.Select((v, i) => new Point(i * w / Math.Max(1, ft.Count - 1), Y(v))).ToList();
        // área com degradê sob a linha
        var area = new List<Point>(pts) { new(w, h), new(0, h) };
        Graph.Children.Add(new Avalonia.Controls.Shapes.Polygon
        {
            Points = area,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#556F98D8"), 0), new GradientStop(Color.Parse("#006F98D8"), 1) },
            },
        });
        Graph.Children.Add(new Avalonia.Controls.Shapes.Polyline { Points = pts, Stroke = Br("TvLapis"), StrokeThickness = 2.5, StrokeJoin = PenLineJoin.Round });
    }

    /// <summary>Os valores do botão de limite, na ordem em que o A anda (com geração ligada, o limite é dos quadros reais).</summary>
    private List<double> LimitSteps()
    {
        int mult = _game != null ? _s.FgApplied.GetValueOrDefault(_game.Key, 1) : 1;
        return FrameLimiter.Candidates(ScreenHz, _screen?.Vrr == true).Select(v => Math.Round(v / mult, 2)).Append(0).Distinct().ToList();
    }

    private double NextLimit()
    {
        var v = LimitSteps();
        int i = v.FindIndex(x => Math.Abs(x - Limit()) < 0.01);
        return v[(i + 1) % v.Count];
    }

    private string NextLimitText() => NextLimit() is double n && n > 0 ? $"{n:0.##} FPS" : "sem limite";

    private double ScreenHz => _screen?.Hz ?? 60;

    private double Limit() => _game != null && _s.FpsLimits.TryGetValue(_game.Key, out var l) ? l : 0;

    private void ShowTiles()
    {
        void T(string n, string value, string hint, bool enabled = true, bool on = false)
        {
            var t = _tile[n];
            t.Value.Text = value;
            t.Hint.Text = hint;
            t.Button.IsEnabled = enabled;
            t.Icon.Classes.Set("on", on && enabled); // ligado: o ícone acende em lápis-lazúli
        }
        bool has = _game != null;
        GameChip.Opacity = has ? 0 : 1; // sem jogo: a seção fica apagada e a etiqueta diz por quê
        double lim = Limit();
        int mult = has ? _s.FgApplied.GetValueOrDefault(_game!.Key, 1) : 1; // valendo agora
        int want = has ? Lsfg.Multiplier(_s, _game!.Key) : 1;             // pedida (vale ao reabrir)
        string? fgMissing = Lsfg.Missing();
        bool limited = has && _s.FpsLimits.ContainsKey(_game!.Key);
        bool hidden = has && (_s.OverlayHidden.TryGetValue(_game!.Key, out bool h) ? h : ConsoleSetup.MangoHidden(Sys.Read(MangoHudConfig.FilePath) ?? ""));
        bool light = has && _s.FgLight.Contains(_game!.Key);
        T("limit", "Mudar limite", has ? $"próximo: {NextLimitText()}" : "próximo valor da TV", has, lim > 0);
        T("fg", want >= 2 ? (Lsfg.Choices(ScreenHz).Max() > want ? $"Usar {want + 1}×" : "Desligar") : "Ligar 2×",
          fgMissing ?? (want != mult ? "vale ao reabrir o jogo" : want >= 2 ? $"{ScreenHz / want:0.##} reais × {want}" : "2× = metade reais"),
          has && fgMissing == null, want >= 2);
        T("fglight", light ? "Modo normal" : "Modo leve", has && want < 2 ? "com geração ligada" : light ? "imagem melhor" : "mais rápido",
          has && want >= 2 && fgMissing == null, light);
        bool fast = has && _s.FastVsync.Contains(_game!.Key);
        bool vsyncPending = has && _vsyncAtLaunch.TryGetValue(_game!.Pid, out bool was) && was != fast;
        T("vsync", fast ? "Usar normal" : "Usar rápido",
          vsyncPending ? "vale ao reabrir" : fast ? "não cai para 30" : "evita cair p/ 30", has, fast);
        T("overlay", hidden ? "Mostrar FPS" : "Esconder FPS", has && !limited ? "precisa de limite" : "segue medindo", limited, has && !hidden);
        T("power", _power == "performance" ? "Desempenho" : _power == null ? "—" : "Equilibrado",
          _power == null ? "sem power-profiles-daemon" : _power == "performance" ? "trocar para equilibrado" : "trocar para desempenho", _power != null, _power == "performance");
        T("audio", _sink != null ? QuickActions.Short(_sink) : "—", "trocar a saída", _sink != null, true);
        T("screens", _screensOff ? "Ligar telas" : "Desligar telas", _screensOff ? "as outras estão desligadas" : "deixa só a tela do jogo", on: _screensOff);
        T("pad", _padConfirm ? "Confirmar" : "Desligar", _padConfirm ? "A de novo desliga" : "desconecta o Bluetooth");
        T("shot", "Tirar foto", "fecha o menu e captura");
        T("bp", "Big Picture", "o jogo continua aberto");
        bool paper = _s.TvTheme == "light";
        T("theme", paper ? "Claro" : "Escuro", paper ? "trocar para escuro" : "trocar para claro", on: paper);
        T("scale", $"{_s.TvScale:0%}", $"próximo: {NextScale():0%}", on: _s.TvScale != 1);
        T("motion", _s.TvMotion ? "Ligadas" : "Desligadas", _s.TvMotion ? "desligar entrada e molas" : "ligar entrada e molas", on: _s.TvMotion);
        bool custom = paper || _s.TvScale != 1 || !_s.TvMotion;
        T("reset", "Restaurar", custom ? "escuro, 100%, animado" : "já está no padrão", custom);
    }

    private async Task ReadAudio()
    {
        var sinks = await QuickActions.ReadSinks();
        _sink = sinks.FirstOrDefault(x => x.Default)?.Name;
        _volume = await QuickActions.Volume();
        ShowTiles();
    }

    private async Task ReadPower()
    {
        _power = await Task.Run(BatteryInfo.PowerProfile);
        ShowTiles();
    }

    private async void Activate(string name)
    {
        var g = _game;
        if (name != "pad") _padConfirm = false;
        try
        {
            switch (name)
            {
                case "limit" when g != null:
                    FrameLimiter.Set(_s, g.Key, NextLimit());
                    break;
                case "fg" when g != null:
                    // desligada → 2× → 3× (se der ≥ 30 reais na tela) → desligada; ligar/desligar vale ao reabrir, trocar vale já
                    var steps = new List<int> { 1 }.Concat(Lsfg.Choices(ScreenHz)).ToList();
                    int cur = Lsfg.Multiplier(_s, g.Key);
                    Lsfg.Set(_s, g.Key, steps[(steps.IndexOf(cur) + 1) % steps.Count], ScreenHz, running: true);
                    break;
                case "fglight" when g != null:
                    Lsfg.SetLight(_s, g.Key, !_s.FgLight.Contains(g.Key), ScreenHz);
                    break;
                case "vsync" when g != null:
                    if (!_s.FastVsync.Remove(g.Key)) _s.FastVsync.Add(g.Key);
                    _s.FpsLimits.TryAdd(g.Key, 0); // o arquivo do jogo precisa de um limite: 0 = sem limite
                    _s.Save();
                    FrameLimiter.Write(_s, g.Key);
                    break;
                case "overlay" when g != null:
                    bool hid = _s.OverlayHidden.TryGetValue(g.Key, out bool hv) ? hv : ConsoleSetup.MangoHidden(Sys.Read(MangoHudConfig.FilePath) ?? "");
                    _s.OverlayHidden[g.Key] = !hid;
                    _s.Save();
                    FrameLimiter.Write(_s, g.Key); // o MangoHud relê; o log volta pelo control= novo
                    break;
                case "audio":
                    await QuickActions.NextSink();
                    await ReadAudio();
                    break;
                case "screens":
                    await QuickActions.OtherScreens(!_screensOff, _screen?.Name);
                    _screensOff = !_screensOff;
                    break;
                case "pad" when !_padConfirm:
                    _padConfirm = true; // desligar o controle tira o jeito de mexer no menu: pede confirmação
                    break;
                case "pad":
                    _padConfirm = false;
                    await QuickActions.DisconnectPads();
                    Dismiss();
                    return;
                case "power" when _power != null:
                    await Sys.RunAsync("powerprofilesctl", "set", _power == "performance" ? "balanced" : "performance");
                    await ReadPower();
                    break;
                case "shot":
                    // depois que o menu fecha e o jogo volta à frente
                    await Sys.RunAsync("systemd-run", "--user", "--collect", "--quiet", "sh", "-c", $"sleep 1; exec {Sys.Quote(ConsoleSetup.Bin("controller-screenshot"))}");
                    Dismiss();
                    return;
                case "bp":
                    await Sys.RunAsync("systemd-run", "--user", "--collect", "--quiet", ConsoleSetup.HydraWrapper, "--big-picture");
                    if (_overlay) { Dismiss(); return; } // o overlay fica por cima de tudo: sai da frente do Big Picture
                    break;
                case "theme":
                    _s.TvTheme = _s.TvTheme == "light" ? "dark" : "light";
                    SaveLook();
                    break;
                case "scale":
                    _s.TvScale = NextScale();
                    SaveLook();
                    break;
                case "motion":
                    _s.TvMotion = !_s.TvMotion;
                    SaveLook();
                    break;
                case "reset":
                    (_s.TvTheme, _s.TvScale, _s.TvMotion) = ("dark", 1.0, true);
                    SaveLook();
                    _tile["theme"].Button.Focus(); // o Restaurar fica desligado: o foco não pode sumir
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
        if (nav == PadNav.Menu) { Dismiss(); return; }
        if (nav == PadNav.Back) { if (SettingsPanel.IsVisible) ShowSettings(false); else Dismiss(); return; }
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

    private void Close_Click(object? sender, RoutedEventArgs e) => Dismiss();

    /// <summary>Entrada a cada vez que a janela aparece (o Opened pode vir de novo no Show do overlay: roda uma vez só).</summary>
    private void Enter()
    {
        if (_entered) return;
        _entered = true;
        if (SettingsPanel.IsVisible) ShowSettings(false);
        // depois do 1º quadro: na abertura a frio os tempos da cascata corriam antes do desenho e botões ficavam apagados
        RequestAnimationFrame(_ =>
        {
            Intro();
            // só se nada tem foco: quem já andou com o direcional nesse meio-tempo não perde o lugar
            if (!this.GetVisualDescendants().OfType<Button>().Any(b => b.IsFocused)) FirstEnabled()?.Focus();
        });
        if (_overlay) _pad.Grab = true;
    }

    /// <summary>Overlay: some e solta o controle (o processo continua, para abrir na hora); janela comum: fecha.</summary>
    private void Dismiss()
    {
        if (!_overlay) { Close(); return; }
        _padConfirm = false;
        _entered = false;
        _pad.Grab = false;
        // apaga o conteúdo e só esconde depois de desenhar: o XWayland guarda o último quadro e a próxima abertura
        // mostraria o menu velho por um instante antes do esmaecer
        Zoom.Transitions = null;
        Zoom.Opacity = 0;
        RequestAnimationFrame(_ => RequestAnimationFrame(_ => { if (!_entered) Hide(); }));
    }

    private void Toggle()
    {
        if (IsVisible && _entered) { Dismiss(); return; }
        OverlayRule(_screen?.Name); // recarregar a config do Hyprland apaga a regra; _screen é a tela do jogo (ou a em foco)
        _ticks = 0;
        Show();
        Enter();
        Tick();
    }

    /// <summary>Quem limitou, curto, para a linha da sessão anterior.</summary>
    private static string Who(Limiter l) => l switch
    {
        Limiter.MainThread => "thread do jogo", Limiter.Gpu => "GPU", Limiter.Cpu => "CPU", Limiter.Thermal => "temperatura",
        Limiter.Vram => "VRAM", Limiter.Ram => "RAM", _ => "nada no limite",
    };

    /// <summary>
    /// Regra do Hyprland para o overlay: flutuante, fixo na tela do jogo, do tamanho dela, sem receber foco ao
    /// aparecer. Assim fica por cima do jogo em tela cheia e o jogo continua com o foco. Com nome: aplicar de novo só atualiza.
    /// </summary>
    private static void OverlayRule(string? monitor = null) =>
        Sys.RunAsync("hyprctl", "eval", "hl.window_rule({ name = \"pharos-gaming-overlay\", match = { title = \"^" + OverlayTitle + "$\" }, " +
            // na tela do jogo, não na que tem foco (o monitor em pé ao lado espremeria o menu); só nomes simples entram no Lua
            (monitor is { Length: > 0 } && monitor.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? $"monitor = \"{monitor}\", " : "") +
            "float = true, pin = true, no_initial_focus = true, no_dim = true, no_blur = true, no_anim = true, no_shadow = true, " +
            "border_size = 0, rounding = 0, tag = \"-default-opacity\", opacity = \"1 1\", size = { \"monitor_w\", \"monitor_h\" }, move = { 0, 0 } })")
            .GetAwaiter().GetResult();

    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowSettings(!SettingsPanel.IsVisible);

    /// <summary>Troca a coluna da direita entre os ajustes do jogo e os do menu; B volta.</summary>
    private void ShowSettings(bool on)
    {
        SettingsPanel.IsVisible = on;
        MainPanel.IsVisible = !on;
        SettingsText.Text = on ? "Fechar ajustes" : "Ajustes";
        BackHint.Text = on ? "Voltar ao menu" : "Voltar ao jogo";
        (on ? _tile["theme"].Button : BtnSettings).Focus(NavigationMethod.Directional);
    }

    private double NextScale() => Scales[(Array.FindIndex(Scales, x => Math.Abs(x - _s.TvScale) < 0.01) + 1) % Scales.Length];

    private void SaveLook()
    {
        _s.Save();
        ApplyLook();
    }

    /// <summary>Tema, tamanho e animações do menu (AppSettings.TvTheme, TvScale, TvMotion).</summary>
    private void ApplyLook()
    {
        RequestedThemeVariant = _s.TvTheme == "light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        double k = Math.Clamp(_s.TvScale, 0.5, 2); // settings.json pode vir editado à mão
        Zoom.LayoutTransform = new ScaleTransform(k, k);
        Zoom.Classes.Set("still", !_s.TvMotion);
        // overlay: o fundo deixa o jogo aparecer atrás (o vidro dos cartões já é translúcido)
        if (_overlay && Br("TvBg") is ISolidColorBrush bg) Background = new SolidColorBrush(bg.Color, 0.84);
    }
}
