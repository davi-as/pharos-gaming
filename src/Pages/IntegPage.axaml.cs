using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace PharosGaming;

/// <summary>Página Integração (PHAROS INTEGRAÇÃO): efeitos de iluminação em todos os dispositivos do OpenRGB.</summary>
public partial class IntegPage : UserControl
{
    private readonly OpenRgbClient _orgb = new();
    private List<LightDevice> _devices = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, ((byte, byte, byte)[] Colors, long At)> _last = new();
    private readonly Queue<string> _diag = new();
    private string _effect = "static";
    private (byte R, byte G, byte B) _color = (255, 255, 255);
    private float _smooth = float.NaN;
    private bool _running, _busy;

    private static readonly (string Name, byte R, byte G, byte B)[] SwatchColors =
    {
        ("Branco", 255, 255, 255), ("Vermelho", 255, 0, 0), ("Laranja", 255, 110, 0), ("Amarelo", 255, 210, 0),
        ("Verde", 0, 255, 60), ("Ciano", 0, 220, 255), ("Azul", 0, 80, 255), ("Roxo", 150, 0, 255), ("Rosa", 255, 0, 160),
    };

    public IntegPage()
    {
        InitializeComponent();
        foreach (var (name, r, g, b) in SwatchColors)
        {
            var btn = new Button { Width = 26, Height = 26, Margin = new(0, 0, 6, 6), CornerRadius = new(13), Padding = new(0),
                                   Background = new SolidColorBrush(Color.FromRgb(r, g, b)), BorderThickness = new(1) };
            ToolTip.SetTip(btn, name);
            btn.Click += (_, _) => { R.Value = r; G.Value = g; B.Value = b; };
            Swatches.Children.Add(btn);
        }
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        BtnServer.IsEnabled = Sys.Has("openrgb");
        if (!BtnServer.IsEnabled) ToolTip.SetTip(BtnServer, "Instale o OpenRGB (pacote openrgb da sua distro ou o Flatpak org.openrgb.OpenRGB).");
        Color_Changed(null, null);
        _ = ConnectAsync();
    }

    private void Log(string line)
    {
        _diag.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_diag.Count > 80) _diag.Dequeue();
        Diag.Text = string.Join("\n", _diag);
    }

    private async Task ConnectAsync()
    {
        if (_busy) return;
        _busy = true;
        BtnConnect.IsEnabled = false;
        try
        {
            var (devs, status) = await Task.Run(() => _orgb.Connect());
            _devices = devs;
            Devices.ItemsSource = devs;
            OrgbStatus.Text = devs.Count > 0 ? "OpenRGB: " + status
                : "OpenRGB: " + status + " Abra o OpenRGB e ligue o servidor (aba SDK Server › Start Server) ou use o botão abaixo. " +
                  "Para placa-mãe e memória o OpenRGB precisa do módulo i2c-dev carregado e das regras udev dele (o pacote da distro já instala).";
            DevicesHint.Text = devs.Count == 0 ? "Nenhum dispositivo ainda."
                : $"{devs.Count} dispositivo(s), {devs.Sum(d => d.LedCount)} LEDs no total. Desmarque o que não quer controlar.";
            Log("OpenRGB · " + status);
            foreach (var d in devs) Log($"  {d.Name} · {d.Detail}");
        }
        finally { _busy = false; BtnConnect.IsEnabled = true; }
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) Stop();
        await ConnectAsync();
    }

    private async void Server_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("openrgb", "--server --startminimized") { UseShellExecute = false })?.Dispose();
            OrgbStatus.Text = "OpenRGB: abrindo o servidor (detectando os dispositivos leva alguns segundos)…";
            await Task.Delay(6000);
            await ConnectAsync();
        }
        catch (Exception ex) { OrgbStatus.Text = "OpenRGB: não consegui abrir: " + ex.Message; }
    }

    private void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) { Stop(); return; }
        var sel = _devices.Where(d => d.Selected && d.LedCount > 0).ToList();
        if (sel.Count == 0) { RunHint.Text = "Conecte e marque pelo menos um dispositivo com LEDs."; return; }
        var fail = sel.Where(d => !_orgb.Acquire(d)).Select(d => d.Name).ToList();
        foreach (var d in sel) Log($"Controle de \"{d.Name}\": {(fail.Contains(d.Name) ? "falhou" : "ok")}");
        _last.Clear();
        _smooth = float.NaN;
        _running = true;
        BtnRun.Content = "DEVOLVER AO ORIGINAL";
        RunHint.Text = fail.Count == 0 ? $"Controlando {sel.Count} dispositivo(s)." : "Controle ativo, mas sem resposta de: " + string.Join(", ", fail);
        _timer.Start();
    }

    private void Stop()
    {
        _timer.Stop();
        _running = false;
        BtnRun.Content = "ATIVAR CONTROLE";
        LiveText.Text = "";
        foreach (var d in _devices.Where(d => d.Selected)) _orgb.Release(d);
        RunHint.Text = "Controle devolvido: os dispositivos voltaram ao modo que estavam.";
    }

    private void Fx_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true, Tag: string fx }) return;
        _effect = fx;
        _smooth = float.NaN;
        _last.Clear();
    }

    private void Color_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs? e)
    {
        if (Preview == null || BrightText == null) return;
        _color = ((byte)R.Value, (byte)G.Value, (byte)B.Value);
        Preview.Background = new SolidColorBrush(Color.FromRgb(_color.R, _color.G, _color.B));
        BrightText.Text = $"{Bright.Value:0}%";
        SpeedText.Text = $"{Speed.Value:0}";
        _last.Clear();
    }

    /// <summary>Valor ao vivo que dirige o efeito (temperatura ou carga), suavizado. Null se o sensor não responde.</summary>
    private float? LiveValue()
    {
        float? raw = _effect switch { "cpu" => Live.Cpu?.Temp, "gpu" => Live.Gpu?.Temp, "load" => Live.Cpu?.Total, _ => null };
        if (raw == null) return null;
        _smooth = float.IsNaN(_smooth) ? raw.Value : _smooth + (raw.Value - _smooth) * 0.08f;
        return _smooth;
    }

    private void Tick()
    {
        (byte, byte, byte)? live = null;
        if (_effect is "cpu" or "gpu" or "load")
        {
            float? v = LiveValue();
            LiveText.Text = v == null ? "Sensor indisponível." : _effect == "load" ? $"CPU {v:0}% de carga" : $"{(_effect == "cpu" ? "CPU" : "GPU")} {v:0} °C";
            if (v != null) live = LightEffects.LiveColor(_effect, v.Value);
        }
        else LiveText.Text = "";
        long now = Environment.TickCount64;
        double t = _clock.Elapsed.TotalSeconds;
        foreach (var d in _devices.Where(d => d.Selected && d.LedCount > 0))
        {
            bool have = _last.TryGetValue(d.Index, out var last);
            if (have && now - last.At < 25) continue; // o OpenRGB não aguenta mais que ~40 envios/s por dispositivo
            var cols = LightEffects.Frame(_effect, t, d.LedX, _color, Bright.Value / 100.0, Speed.Value, live);
            // só reenvia se mudou (ou a cada 2 s, para outro programa não retomar as luzes)
            if (have && now - last.At < 2000 && last.Colors.AsSpan().SequenceEqual(cols)) continue;
            if (!_orgb.Send(d, cols)) RunHint.Text = $"{d.Name}: falha de comunicação com o OpenRGB.";
            _last[d.Index] = (cols, now);
        }
    }
}
