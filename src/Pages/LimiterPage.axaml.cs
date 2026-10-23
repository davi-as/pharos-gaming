using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PharosGaming;

/// <summary>Página Limitador de FPS: limite por jogo com frame pacing (MangoHud, modo early), sugestão pela tela e veredito ao vivo.</summary>
public partial class LimiterPage : UserControl
{
    private readonly AppSettings _s = AppSettings.Current;
    private RunningGame? _running;
    private FrameLimiter.Screen? _screen;
    private string? _target;          // jogo que os botões alteram: o aberto ou um da lista
    private int _ticks;
    private bool _scanning;

    public LimiterPage()
    {
        InitializeComponent();
        try { FrameLimiter.RewriteAll(_s); } // o MangoHud.conf do usuário pode ter mudado (GOverlay) desde a última vez
        catch (Exception ex) { WriteNote.Text = "Não consegui atualizar as configs por jogo: " + ex.Message; }
        ShowSaved();
        ShowTarget();
        Live.Tick += Tick;
    }

    private async void Tick()
    {
        if (_ticks++ % 4 == 0 && !_scanning) // processos a cada 2 s
        {
            _scanning = true;
            var (game, screen) = await Task.Run(() =>
            {
                var g = FrameLimiter.Pick(FrameLimiter.Running(), FrameLimiter.WindowPids());
                return (g, g == null ? null : FrameLimiter.ScreenOf(g.Pid));
            });
            _scanning = false;
            if (game?.Key != _running?.Key)
            {
                _running = game;
                _screen = screen;
                if (game != null) _target = game.Key;
                ShowTarget();
            }
        }
        ShowLive();
    }

    private void ShowLive()
    {
        var f = _running != null ? Live.Frames : null;
        LiveRow.IsVisible = _running != null;
        FpsText.Text = f != null ? $"{f.Fps:N0}" : "—";
        FtText.Text = f != null ? $"{f.FrameTimeMs:N1} ms" : "—";
        var v = f != null ? FrameLimiter.Judge(Live.FrameTimes, Limit(_running!.Key)) : null;
        VerdictTitle.Text = v?.Title ?? (f == null ? "Sem dados de quadros" : "Medindo…");
        VerdictTitle.Foreground = v == null ? App.Brush("Muted") : v.Good ? App.Brush("Ok") : App.Brush("Bad");
        // com um limite salvo, o arquivo do jogo traz o control= e o PHAROS liga o log sozinho (MangoHudLog)
        VerdictDetail.Text = v?.Detail ?? (f == null
            ? "O veredito usa o log do MangoHud: ele começa sozinho quando o jogo tem um limite salvo aqui (ou com “FPS no PHAROS” ligado em PC como console)."
            : "");

        var choices = Candidates();
        SuggestText.Text = f != null && FrameLimiter.Suggest(choices, f.MinFps) is double sug
            ? $"Sugestão: {Fmt(sug)} FPS. O mínimo dos últimos 10 s foi {f.MinFps:N0}, e esse é o maior valor da lista que ele segura com folga."
            : "A sugestão aparece com o jogo aberto: o PHAROS mede o mínimo de FPS e escolhe o maior valor da lista que o PC segura.";
    }

    private void ShowTarget()
    {
        LimitCard.IsEnabled = _target != null;
        if (_running != null)
        {
            GameName.Text = _running.Name;
            GameNote.Text = "Aberto agora" + (_screen is { } sc ? $" · tela {sc.Name}, {sc.Hz:0.#} Hz" + (sc.Vrr ? " com VRR" : " sem VRR") : "");
        }
        else if (_target != null)
        {
            GameName.Text = FrameLimiter.Display(_target);
            GameNote.Text = "Não está aberto: o limite vale na próxima vez que o jogo abrir.";
        }
        else
        {
            GameName.Text = "Nenhum jogo aberto";
            GameNote.Text = "Abra o jogo pelo Hydra (ou com o MangoHud ligado) e ele aparece aqui sozinho. Para mudar um jogo salvo, escolha na lista.";
        }
        CurrentText.Text = _target == null ? "" : Limit(_target) is double l and > 0 ? $"Limite atual: {Fmt(l)} FPS" : "Sem limite";

        Choices.Children.Clear();
        foreach (var c in Candidates())
        {
            var b = new Button { Content = Fmt(c), Margin = new(0, 0, 6, 6), MinWidth = 56, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            if (_target != null && Limit(_target) is double cur && Math.Abs(cur - c) < 0.01) b.Classes.Add("accent");
            b.Click += (_, _) => Apply(c);
            Choices.Children.Add(b);
        }
    }

    private List<double> Candidates() => _screen is { } sc ? FrameLimiter.Candidates(sc.Hz, sc.Vrr) : new() { 144, 120, 60, 40, 30 };

    private double Limit(string key) => _s.FpsLimits.TryGetValue(key, out var l) ? l : 0;

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

    private void Apply(double limit)
    {
        if (_target == null) return;
        try
        {
            FrameLimiter.Set(_s, _target, limit);
            WriteNote.Text = $"Gravado em {FrameLimiter.FileFor(_target)}." + (_running?.Key == _target ? " Já vale no jogo aberto." : "");
        }
        catch (Exception ex) { WriteNote.Text = "Não consegui gravar: " + ex.Message; }
        ShowTarget();
        ShowSaved();
    }

    private void Custom_Click(object? sender, RoutedEventArgs e)
    {
        string t = (CustomFps.Text ?? "").Trim().Replace(',', '.');
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v is >= 10 and <= 1000) Apply(v);
        else WriteNote.Text = "Digite um número entre 10 e 1000.";
    }

    private void NoLimit_Click(object? sender, RoutedEventArgs e) => Apply(0);

    private void ShowSaved()
    {
        Saved.Children.Clear();
        EmptyList.IsVisible = _s.FpsLimits.Count == 0;
        foreach (var (key, limit) in _s.FpsLimits.OrderBy(p => FrameLimiter.Display(p.Key), StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto") };
            row.Children.Add(new TextBlock { Text = FrameLimiter.Display(key), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var lim = new TextBlock { Text = limit > 0 ? $"{Fmt(limit)} FPS" : "sem limite", Classes = { "dim" }, Margin = new(12, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(lim, 1);
            var pick = new Button { Content = "Alterar", FontSize = 11 };
            pick.Click += (_, _) => { _target = key; ShowTarget(); };
            Grid.SetColumn(pick, 2);
            var del = new Button { Content = "Remover", FontSize = 11, Margin = new(6, 0, 0, 0) };
            del.Click += (_, _) =>
            {
                try { FrameLimiter.Remove(_s, key); WriteNote.Text = ""; }
                catch (Exception ex) { WriteNote.Text = "Não consegui remover: " + ex.Message; }
                if (_target == key && _running?.Key != key) _target = null;
                ShowTarget();
                ShowSaved();
            };
            Grid.SetColumn(del, 3);
            row.Children.Add(lim);
            row.Children.Add(pick);
            row.Children.Add(del);
            Saved.Children.Add(row);
        }
    }
}
