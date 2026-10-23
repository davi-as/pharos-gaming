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

    public static CpuReading? Cpu { get; private set; }
    public static GpuReading? Gpu { get; private set; }
    public static MemReading? Mem { get; private set; }
    public static FrameStats? Frames { get; private set; }

    /// <summary>Pausa as leituras de GPU (benchmarks de GPU: cada consulta ao driver custa um engasgo).</summary>
    public static bool Paused { get; set; }

    public static event Action? Tick;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSec) };
        _timer.Tick += (_, _) => Step();
        _timer.Start();
    }

    /// <summary>Uma leitura, já avisando as páginas (o timer chama; os testes também).</summary>
    public static void Step()
    {
        if (Paused) return;
        (Cpu, Gpu, Mem) = Sensors.Sample();
        Frames = FramesReader.Poll();
        Tick?.Invoke();
    }

    public static LoadSample Sample() => BottleneckAnalyzer.Sample(Cpu!, Gpu, Mem!, Frames);
}
