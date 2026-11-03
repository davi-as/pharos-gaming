using Avalonia.Threading;

namespace PharosGaming;

/// <summary>
/// Leitura ao vivo compartilhada por todas as páginas: uma amostra de CPU/GPU/RAM e FPS a cada meio segundo.
/// Cada página assina <see cref="Tick"/> em vez de ter o próprio timer (a carga da CPU é a diferença entre leituras,
/// então só pode haver um leitor).
/// </summary>
public static class Live
{
    public const double TickSec = 0.5;

    private static readonly LinuxSensors Sensors = new();
    private static readonly MangoHudFrames FramesReader = new();
    private static DispatcherTimer? _timer;
    private static Timer? _logTimer;

    public static CpuReading? Cpu { get; private set; }
    public static GpuReading? Gpu { get; private set; }
    public static MemReading? Mem { get; private set; }
    public static FrameStats? Frames { get; private set; }
    public static IReadOnlyList<double> FrameTimes => FramesReader.FrameTimes;
    public static Floor SessionFloor => FramesReader.SessionFloor;
    public static IReadOnlyList<double> RecentFrameTimes => FramesReader.RecentFrameTimes;
    public static DateTime? SessionStart => FramesReader.SessionStart;

    /// <summary>O jogo aberto (o com janela entre os que têm MangoHud), visto a cada 3 s fora da interface.</summary>
    public static RunningGame? Game { get; private set; }
    /// <summary>Thread mais ocupada do jogo, em % de um núcleo: limita o FPS mesmo com a CPU total "sobrando".</summary>
    public static float? GameThread { get; private set; }
    /// <summary>Gargalo da sessão (a do log do MangoHud em leitura), uma amostra por segundo.</summary>
    public static BottleneckSession? Bottleneck { get; private set; }
    private static readonly ThreadLoad Threads = new();
    private static string? _bnFile;
    private static int _steps;

    /// <summary>Pausa as leituras de GPU (benchmarks de GPU: cada consulta ao driver custa um engasgo).</summary>
    public static bool Paused { get; set; }

    public static event Action? Tick;

    /// <summary>O histórico de sessões pode ter mudado (chamado fora da thread da interface).</summary>
    public static event Action? SessionsChanged;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSec) };
        _timer.Tick += (_, _) => Step();
        _timer.Start();
        // fora da interface: varre /proc e liga o log do MangoHud dos jogos abertos (overlay escondido)
        int n = 0;
        _logTimer = new Timer(_ =>
        {
            try { Game = MangoHudLog.Tick(); } catch (Exception) { }
            if (n++ % 10 == 0) // a cada 30 s: logs que terminaram viram sessão no histórico
                try { SessionLog.Scan(MangoHudFrames.Folders()); SessionsChanged?.Invoke(); } catch (Exception) { }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    /// <summary>Uma leitura, já avisando as páginas (o timer chama; os testes também).</summary>
    public static void Step()
    {
        if (Paused) return;
        (Cpu, Gpu, Mem) = Sensors.Sample();
        Frames = FramesReader.Poll();
        if (_steps++ % 2 == 0) SampleBottleneck();
        Tick?.Invoke();
    }

    public static LoadSample Sample() => BottleneckAnalyzer.Sample(Cpu!, Gpu, Mem!, Frames) with { GameThread = Frames != null ? GameThread : null };

    /// <summary>Uma amostra por segundo enquanto o jogo grava FPS; a contagem vai para o lado do CSV a cada 30 (histórico).</summary>
    private static void SampleBottleneck()
    {
        var g = Game;
        string? file = FramesReader.CurrentFile;
        if (g == null || Frames == null || file == null || Cpu == null || Mem == null) { GameThread = null; return; }
        GameThread = Threads.Sample(g.Pid);
        if (_bnFile != file) (Bottleneck, _bnFile) = (new BottleneckSession(), file);
        if (GameThread == null) return;
        Bottleneck!.Add(Sample());
        if (Bottleneck.Count % 30 == 0) Bottleneck.Save(file);
    }
}
