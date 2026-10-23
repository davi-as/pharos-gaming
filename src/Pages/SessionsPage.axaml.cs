using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Página Sessões: o histórico do SessionLog, por jogo, cada sessão comparada com a anterior do mesmo jogo.</summary>
public partial class SessionsPage : UserControl
{
    private string? _game; // null = todos

    public SessionsPage()
    {
        InitializeComponent();
        Live.SessionsChanged += () => Dispatcher.UIThread.Post(Show);
        Show();
    }

    private void Show()
    {
        var all = SessionLog.Load().Sessions;
        Empty.IsVisible = all.Count == 0;
        Games.Children.Clear();
        if (all.Count > 0)
        {
            AddChip("Todos", null);
            foreach (var g in all.GroupBy(s => s.Game).OrderByDescending(g => g.Max(s => s.Start)))
                AddChip(g.Key, g.Key);
        }
        var shown = all.Where(s => _game == null || s.Game == _game).OrderByDescending(s => s.Start).ToList();
        Count.Text = all.Count == 0 ? "" : $"{shown.Count} sessão(ões) · {shown.Sum(s => s.Minutes) / 60:0.#} h de jogo";
        List.Children.Clear();
        foreach (var s in shown.Take(100))
            List.Children.Add(Card(s, all.Where(p => p.Game == s.Game && p.Start < s.Start).MaxBy(p => p.Start)));
    }

    private void AddChip(string label, string? game)
    {
        var rb = new RadioButton { Content = label, GroupName = "sessgame", IsChecked = _game == game, Margin = new(0, 0, 14, 0) };
        rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true && _game != game) { _game = game; Show(); } };
        Games.Children.Add(rb);
    }

    private static Control Card(GameSession s, GameSession? prev)
    {
        var head = new Grid { ColumnDefinitions = new("*,Auto") };
        var title = new StackPanel { Spacing = 1 };
        title.Children.Add(new TextBlock { Text = s.Game, Classes = { "h2" } });
        title.Children.Add(new TextBlock
        {
            Text = $"{s.Start:dd/MM/yyyy HH:mm} · {Duration(s.Minutes)}" + (s.Limit > 0 ? $" · limite {s.Limit:0.#} FPS" : ""),
            Classes = { "dim" }, FontSize = 12,
        });
        head.Children.Add(title);

        var metrics = new WrapPanel { Margin = new(0, 12, 0, 0) };
        metrics.Children.Add(Metric("FPS MÉDIO", $"{s.AvgFps:0}", Delta(s.AvgFps, prev?.AvgFps)));
        metrics.Children.Add(Metric("1% LOW", $"{s.Low1Fps:0}", Delta(s.Low1Fps, prev?.Low1Fps)));
        metrics.Children.Add(Metric("RITMO", $"±{s.PacingMs:0.0} ms", Delta(-s.PacingMs, prev == null ? null : -prev.PacingMs, "ms", 0.3)));
        if (s.CpuTempMax is double ct) metrics.Children.Add(Metric("CPU MÁX.", $"{ct:0} °C", null));
        if (s.GpuTempMax is double gt) metrics.Children.Add(Metric("GPU MÁX.", $"{gt:0} °C", null));
        if (s.GpuLoadAvg is double gl) metrics.Children.Add(Metric("GPU MÉDIA", $"{gl:0}%", null));
        if (s.CpuLoadAvg is double cl) metrics.Children.Add(Metric("CPU MÉDIA", $"{cl:0}%", null));

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(metrics);
        return new Border { Classes = { "card" }, Margin = new(0), Child = body };
    }

    private static string Duration(double min) => min >= 60 ? $"{(int)(min / 60)} h {min % 60:00} min" : $"{min:0} min";

    /// <summary>Diferença para a sessão anterior; só aparece quando passa do ruído (2 FPS / 0,3 ms).</summary>
    private static (double Diff, string Unit, double Noise)? Delta(double now, double? before, string unit = "FPS", double noise = 2) =>
        before is double b && Math.Abs(now - b) >= noise ? (now - b, unit, noise) : null;

    private static Control Metric(string label, string value, (double Diff, string Unit, double Noise)? delta)
    {
        var p = new StackPanel { Margin = new(0, 0, 34, 6), MinWidth = 90 };
        p.Children.Add(new TextBlock { Text = label, Classes = { "label" }, FontSize = 11 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = value, Classes = { "serif" }, FontSize = 22, Foreground = App.Brush("Fg") });
        if (delta is { } d)
        {
            bool better = d.Diff > 0;
            var icon = MainWindow.Icon(better ? "trending-up" : "trending-down", 15);
            icon.Foreground = App.Brush(better ? "Ok" : "Bad");
            var tag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            tag.Children.Add(icon);
            double shownDiff = d.Unit == "ms" ? -d.Diff : d.Diff; // ritmo: menos ms é melhor, mas mostra o valor de verdade
            tag.Children.Add(new TextBlock { Text = $"{shownDiff:+0.#;-0.#} {d.Unit}", FontSize = 11.5, Foreground = App.Brush(better ? "Ok" : "Bad") });
            ToolTip.SetTip(tag, "Comparado com a sessão anterior deste jogo");
            row.Children.Add(tag);
        }
        p.Children.Add(row);
        return p;
    }
}
