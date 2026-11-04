using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PharosGaming;

public enum PadNav { Up, Down, Left, Right, Confirm, Back, Menu }

/// <summary>
/// Controle (Xbox, 8BitDo) para o modo TV, lido direto do evdev, sem prender o controle: o jogo e o daemon do console
/// continuam recebendo tudo. Só gera navegação (direcional, analógico esquerdo, A/B por posição, Guide + View).
/// Com <see cref="Grab"/>, prende o controle (EVIOCGRAB) e só o Pharos recebe: o menu por cima do jogo não mexe no jogo.
/// </summary>
public sealed partial class Gamepad : IDisposable
{
    private const int EvKey = 1, EvAbs = 3;
    private const int AbsX = 0, AbsY = 1, HatX = 16, HatY = 17;
    private const int BtnSouth = 304, BtnEast = 305, BtnStart = 315, BtnSelect = 314, BtnMode = 316, DpadUp = 544, DpadDown = 545, DpadLeft = 546, DpadRight = 547;
    private const int StickOn = 20000, StickOff = 9000; // ±32767: passa de 60% para andar, volta a 27% para rearmar

    private readonly Action<PadNav> _onNav;
    private readonly Dictionary<string, Thread> _readers = new();
    private readonly List<Reader> _open = new();
    private volatile bool _grab;
    private readonly Timer _scan;
    private volatile bool _stop;

    /// <summary>Os testes desligam: o controle de verdade de quem está jogando navegaria o menu no meio do teste.</summary>
    public static bool Off;

    public Gamepad(Action<PadNav> onNav)
    {
        _onNav = onNav;
        _scan = new Timer(_ => Rescan(), null, Off ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>eventN dos controles de jogo (handler js*), sem os sensores de movimento do 8BitDo/Switch.</summary>
    public static List<string> Devices(string procDevices) =>
        procDevices.Split("\n\n").Where(b => JsRe().IsMatch(b) && !b.Contains("(IMU)") && !b.Contains("Motion"))
            .Select(b => EventRe().Match(b)).Where(m => m.Success).Select(m => "/dev/input/" + m.Value).ToList();

    public static bool IsSteamMirror(string? vendorFile) => vendorFile?.Trim() == "28de";

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

    /// <summary>
    /// Prende (true) ou solta os controles. Só troca com todos os botões soltos: o jogo e o daemon do console nunca ficam
    /// com um aperto sem a soltura (o daemon acharia o Guide ainda apertado; o jogo, o botão travado).
    /// </summary>
    public bool Grab
    {
        set
        {
            _grab = value;
            lock (_open) foreach (var r in _open) r.Sync(value);
        }
    }

    private void Read(string dev)
    {
        Reader? r = null;
        try
        {
            using var fs = new FileStream(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            r = new Reader(fs);
            lock (_open) _open.Add(r);
            r.Sync(_grab);
            // o controle virtual da Steam (Steam Input, vendor 28de da Valve) repete cada botão do real: só prende, não navega,
            // senão cada toque anda duas casas. shortcut: um Steam Controller/Deck de verdade também é 28de; separar pelo Sysfs virtual se precisar
            bool mirror = IsSteamMirror(Sys.Read($"/sys/class/input/{Path.GetFileName(dev)}/device/id/vendor"));
            var state = new Stick();
            var buf = new byte[24];
            while (!_stop && fs.Read(buf, 0, 24) == 24)
            {
                int type = BitConverter.ToUInt16(buf, 16), code = BitConverter.ToUInt16(buf, 18), value = BitConverter.ToInt32(buf, 20);
                r.Track(type, code, value, _grab);
                // Guide + View de um controle solto é do daemon (ele abre e fecha o menu); preso, só o Pharos vê
                if (!mirror && Map(type, code, value, state) is { } nav && !_stop && (nav != PadNav.Menu || r.Grabbed)) _onNav(nav);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // desconectou ou sem permissão: o rescan tenta de novo
        finally { if (r != null) lock (_open) _open.Remove(r); } // fechar o arquivo solta o controle
    }

    /// <summary>Um controle aberto: o que está apertado agora e se está preso.</summary>
    private sealed class Reader(FileStream fs)
    {
        private readonly HashSet<int> _down = new();
        public bool Grabbed { get; private set; }

        public void Track(int type, int code, int value, bool want)
        {
            lock (this)
            {
                if (type == EvKey && value == 1) _down.Add(code);
                else if (type == EvKey && value == 0) _down.Remove(code);
                else if (type == EvAbs && code is HatX or HatY) { if (value != 0) _down.Add(-code - 1); else _down.Remove(-code - 1); }
            }
            Sync(want);
        }

        public void Sync(bool want)
        {
            lock (this)
            {
                if (want == Grabbed || _down.Count > 0) return;
                const nuint EvIocGrab = 0x40044590; // _IOW('E', 0x90, int)
                if (ioctl((int)fs.SafeFileHandle.DangerousGetHandle(), EvIocGrab, want ? 1 : 0) == 0 || !want) Grabbed = want;
            }
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, nint arg);

    /// <summary>Analógico vira "tecla": anda uma vez ao passar do limite e só de novo depois de voltar ao centro.</summary>
    public sealed class Stick { public bool X, Y, Guide; }

    /// <summary>Um evento do evdev (struct input_event: tipo, código, valor) → navegação, ou null.</summary>
    public static PadNav? Map(int type, int code, int value, Stick s)
    {
        if (type == EvKey && code == BtnMode && value != 2) { s.Guide = value == 1; return null; }
        if (type == EvKey && code == BtnSelect && value == 1 && s.Guide) return PadNav.Menu;
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
