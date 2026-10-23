using System.Text.RegularExpressions;

namespace PharosGaming;

public enum PadNav { Up, Down, Left, Right, Confirm, Back }

/// <summary>
/// Controle (Xbox, 8BitDo) para o modo TV, lido direto do evdev, sem prender o controle: o jogo e o daemon do console
/// continuam recebendo tudo. Só gera navegação (direcional, analógico esquerdo, A/B por posição).
/// </summary>
public sealed partial class Gamepad : IDisposable
{
    private const int EvKey = 1, EvAbs = 3;
    private const int AbsX = 0, AbsY = 1, HatX = 16, HatY = 17;
    private const int BtnSouth = 304, BtnEast = 305, BtnStart = 315, DpadUp = 544, DpadDown = 545, DpadLeft = 546, DpadRight = 547;
    private const int StickOn = 20000, StickOff = 9000; // ±32767: passa de 60% para andar, volta a 27% para rearmar

    private readonly Action<PadNav> _onNav;
    private readonly Dictionary<string, Thread> _readers = new();
    private readonly Timer _scan;
    private volatile bool _stop;

    public Gamepad(Action<PadNav> onNav)
    {
        _onNav = onNav;
        _scan = new Timer(_ => Rescan(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>eventN dos controles de jogo (handler js*), sem os sensores de movimento do 8BitDo/Switch.</summary>
    public static List<string> Devices(string procDevices) =>
        procDevices.Split("\n\n").Where(b => JsRe().IsMatch(b) && !b.Contains("(IMU)") && !b.Contains("Motion"))
            .Select(b => EventRe().Match(b)).Where(m => m.Success).Select(m => "/dev/input/" + m.Value).ToList();

    [GeneratedRegex(@"H: Handlers=.*\bjs\d+")] private static partial Regex JsRe();
    [GeneratedRegex(@"event\d+")] private static partial Regex EventRe();

    private void Rescan()
    {
        if (_stop) return;
        foreach (var dev in Devices(Sys.Read("/proc/bus/input/devices") ?? ""))
        {
            lock (_readers)
            {
                if (_readers.TryGetValue(dev, out var t) && t.IsAlive) continue;
                var th = new Thread(() => Read(dev)) { IsBackground = true, Name = "gamepad " + dev };
                _readers[dev] = th;
                th.Start();
            }
        }
    }

    private void Read(string dev)
    {
        try
        {
            using var fs = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            var state = new Stick();
            var buf = new byte[24];
            while (!_stop && fs.Read(buf, 0, 24) == 24)
            {
                int type = BitConverter.ToUInt16(buf, 16), code = BitConverter.ToUInt16(buf, 18), value = BitConverter.ToInt32(buf, 20);
                if (Map(type, code, value, state) is { } nav && !_stop) _onNav(nav);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // desconectou ou sem permissão: o rescan tenta de novo
    }

    /// <summary>Analógico vira "tecla": anda uma vez ao passar do limite e só de novo depois de voltar ao centro.</summary>
    public sealed class Stick { public bool X, Y; }

    /// <summary>Um evento do evdev (struct input_event: tipo, código, valor) → navegação, ou null.</summary>
    public static PadNav? Map(int type, int code, int value, Stick s)
    {
        if (type == EvKey && value == 1)
            return code switch
            {
                BtnSouth or BtnStart => PadNav.Confirm, BtnEast => PadNav.Back,
                DpadUp => PadNav.Up, DpadDown => PadNav.Down, DpadLeft => PadNav.Left, DpadRight => PadNav.Right,
                _ => null,
            };
        if (type != EvAbs) return null;
        switch (code)
        {
            case HatX when value != 0: return value < 0 ? PadNav.Left : PadNav.Right;
            case HatY when value != 0: return value < 0 ? PadNav.Up : PadNav.Down;
            case AbsX: return Axis(ref s.X, value, PadNav.Left, PadNav.Right);
            case AbsY: return Axis(ref s.Y, value, PadNav.Up, PadNav.Down);
        }
        return null;
    }

    private static PadNav? Axis(ref bool armed, int v, PadNav neg, PadNav pos)
    {
        if (Math.Abs(v) < StickOff) { armed = false; return null; }
        if (armed || Math.Abs(v) < StickOn) return null;
        armed = true;
        return v < 0 ? neg : pos;
    }

    public void Dispose()
    {
        _stop = true;
        _scan.Dispose();
    }
}
