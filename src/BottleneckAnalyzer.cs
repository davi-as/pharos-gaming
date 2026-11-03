namespace PharosGaming;

public enum Limiter { Gpu, Cpu, Vram, Ram, Thermal, Capped, MainThread }

/// <summary>Uma leitura (1/s) de tudo que pode limitar o FPS.</summary>
public sealed record LoadSample(
    float? GpuLoad, float? CpuTotal, float? CpuMaxCore, string? BusiestCore,
    double? VramPct, float? RamPct, float? GpuTemp, float? CpuTemp, bool GpuThermalCap, double? Fps, float? GameThread = null)
{
    /// <summary>
    /// Regras (na ordem): VRAM cheia → CPU a 95 °C → GPU ≥ 90% (térmico se ela estiver segurando o clock; senão normal em
    /// jogo pesado) → thread mais ocupada do jogo ≥ 80% de um núcleo com a GPU sobrando → CPU/núcleo ≥ 85% → RAM cheia →
    /// nada no limite (trava de FPS, V-Sync ou menu). A GPU quente mas sobrando não é o limite: quem segura é outra coisa.
    /// </summary>
    public Limiter Classify()
    {
        if (VramPct >= 97) return Limiter.Vram;
        if (CpuTemp >= 95) return Limiter.Thermal;
        if (GpuLoad >= 90) return GpuThermalCap ? Limiter.Thermal : Limiter.Gpu;
        // o sistema troca a thread de núcleo: nenhum núcleo parece cheio, mas a thread principal está
        if (GameThread >= 80) return Limiter.MainThread;
        if (CpuMaxCore >= 85 || CpuTotal >= 85) return Limiter.Cpu;
        if (RamPct >= 92) return Limiter.Ram;
        return Limiter.Capped;
    }
}

public static class BottleneckAnalyzer
{
    /// <summary>Junta as leituras do Linux (CPU, GPU, RAM) e do MangoHud (FPS) numa amostra.</summary>
    public static LoadSample Sample(CpuReading cpu, GpuReading? gpu, MemReading mem, FrameStats? frames) =>
        new(gpu?.Load, cpu.Total, cpu.MaxThread, cpu.MaxThreadIndex is int i ? $"thread {i}" : null,
            gpu?.VramPct, mem.Pct, gpu?.Temp, cpu.Temp, gpu?.ThermalCap == true, frames?.Fps);

    public static string Title(Limiter l) => l switch
    {
        Limiter.Gpu => "GPU no limite",
        Limiter.Cpu => "Gargalo de CPU",
        Limiter.Vram => "Memória de vídeo cheia",
        Limiter.Ram => "Memória RAM cheia",
        Limiter.Thermal => "Limitado por temperatura",
        Limiter.MainThread => "Thread principal do jogo",
        _ => "Nada no limite"
    };

    public static string Advice(Limiter l) => l switch
    {
        Limiter.Gpu => "Normal em jogos pesados: a placa de vídeo está sendo bem aproveitada. Para mais FPS, reduza " +
                       "resolução ou qualidade gráfica, ou ative DLSS/FSR.",
        Limiter.Cpu => "A CPU (ou um núcleo dela) não acompanha a GPU. Subir resolução/gráficos quase não custa FPS. " +
                       "Para ganhar FPS: reduza opções que pesam na CPU (distância de visão, multidões, física), feche " +
                       "programas em segundo plano, use o governor de CPU performance e confira se o XMP/EXPO da RAM está ativo na BIOS.",
        Limiter.Vram => "A memória da placa de vídeo encheu, o que causa travadas. Reduza a qualidade das texturas " +
                        "ou a resolução.",
        Limiter.Ram => "Pouca RAM livre: o sistema passa a usar o swap e o jogo engasga. Feche navegadores e outros programas.",
        Limiter.Thermal => "Temperatura alta está derrubando o clock. Verifique ventilação do gabinete, poeira e pasta térmica.",
        Limiter.MainThread => "Uma thread do jogo (a principal) está no limite de um núcleo e a GPU espera por ela; o uso total da " +
                              "CPU parece baixo porque o jogo usa poucas threads. Subir gráficos quase não custa FPS. Para ganhar " +
                              "FPS: reduza o que pesa na CPU (distância de visão, multidões, física, sombras em alguns jogos), use o " +
                              "governor performance e confira o XMP/EXPO da RAM na BIOS.",
        _ => "Nem CPU nem GPU estão no limite: provavelmente há trava de FPS, V-Sync/G-Sync ou limite do MangoHud/gamescope, ou o jogo " +
             "está em menu/pausa. Normal se o FPS já é o desejado."
    };

    /// <summary>O conselho em uma frase, para o menu da TV (cabe em poucas linhas).</summary>
    public static string Short(Limiter l) => l switch
    {
        Limiter.Gpu => "a placa de vídeo está no limite: baixe a qualidade ou ligue upscaling (FSR, XeSS ou DLSS).",
        Limiter.Cpu => "a CPU não acompanha: baixe distância de visão e multidões; gráficos mais altos quase não pesam.",
        Limiter.MainThread => "a GPU espera pela thread principal do jogo. Gráficos mais altos quase não pesam; baixe distância de visão, multidões e sombras.",
        Limiter.Vram => "a memória de vídeo encheu: baixe a qualidade das texturas.",
        Limiter.Ram => "pouca RAM livre: feche navegadores e outros programas.",
        Limiter.Thermal => "a temperatura está derrubando o clock: confira ventilação e poeira.",
        _ => "nada no limite: quedas assim costumam ser carregamento ou compilação de shaders.",
    };

    /// <summary>Equilíbrio teórico pelo desempenho relativo (GPU: RTX 4070 = 100; CPU: Ryzen 7 7700X = 100).</summary>
    public static (double Ratio, string Text) Balance(double gpuIdx, double cpuIdx)
    {
        double ratio = gpuIdx / cpuIdx;
        string text = ratio switch
        {
            > 1.8 => "GPU muito mais forte que a CPU: em boa parte dos jogos a CPU vai limitar o FPS, principalmente em 1080p.",
            > 1.3 => "GPU um pouco à frente da CPU: jogos competitivos em 1080p tendem a ficar limitados pela CPU; " +
                     "em 1440p/4K a GPU volta a ser o limite.",
            >= 0.7 => "Hardware equilibrado: na maioria dos jogos a GPU será o limite, que é o cenário ideal.",
            > 0.4 => "CPU mais forte que a GPU: a placa de vídeo é o limite. Upgrade de GPU traria o maior ganho.",
            _ => "GPU muito fraca para esta CPU: a placa de vídeo limita quase tudo."
        };
        return (ratio, text);
    }
}

/// <summary>Uso da thread mais ocupada de um processo (em % de um núcleo) entre uma chamada e a seguinte.</summary>
public sealed class ThreadLoad
{
    private const double ClkTck = 100; // shortcut: USER_HZ do Linux é 100 em todo kernel comum; ler sysconf se surgir outro
    private Dictionary<int, long> _prev = new();
    private DateTime _at;
    private int _pid;

    /// <summary>utime + stime (campos 14 e 15) de um /proc/.../stat. O nome entre parênteses pode ter espaços.</summary>
    public static long? Ticks(string stat)
    {
        var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return f.Length > 12 && long.TryParse(f[11], out long u) && long.TryParse(f[12], out long k) ? u + k : null;
    }

    /// <summary>Null na 1ª leitura de um processo (precisa de duas) ou se ele sumiu.</summary>
    public float? Sample(int pid)
    {
        var now = new Dictionary<int, long>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories($"/proc/{pid}/task"))
                if (int.TryParse(Path.GetFileName(dir), out int tid) && Sys.Read(Path.Combine(dir, "stat")) is string st && Ticks(st) is long t)
                    now[tid] = t;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var at = DateTime.UtcNow;
        double secs = (at - _at).TotalSeconds;
        float? busiest = pid == _pid && secs > 0.2 && now.Count > 0
            ? (float)(now.Max(kv => _prev.TryGetValue(kv.Key, out long b) ? kv.Value - b : 0) / ClkTck / secs * 100)
            : null;
        (_prev, _at, _pid) = (now, at, pid);
        return busiest;
    }
}

/// <summary>Acumula amostras durante uma sessão de jogo e produz o relatório.</summary>
public sealed class BottleneckSession
{
    private readonly List<(LoadSample Sample, Limiter Limiter)> _samples = new();
    public DateTime Start { get; } = DateTime.Now;
    public TimeSpan Duration { get; init; }
    public int Count => _samples.Count;
    public bool Done => DateTime.Now - Start >= Duration;

    public void Add(LoadSample s) => _samples.Add((s, s.Classify()));

    public IReadOnlyList<(Limiter Limiter, double Pct)> Distribution() =>
        _samples.GroupBy(x => x.Limiter)
                .Select(g => (g.Key, 100.0 * g.Count() / Math.Max(1, _samples.Count)))
                .OrderByDescending(x => x.Item2).ToList();

    public Limiter? Main => _samples.Count == 0 ? null : Distribution()[0].Limiter;

    public double? AvgFps
    {
        get
        {
            var f = _samples.Where(x => x.Sample.Fps > 0).Select(x => x.Sample.Fps!.Value).ToList();
            return f.Count > 0 ? f.Average() : null;
        }
    }

    public double? Avg(Func<LoadSample, float?> sel)
    {
        var v = _samples.Select(x => sel(x.Sample)).Where(x => x != null).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    public double? Max(Func<LoadSample, double?> sel)
    {
        var v = _samples.Select(x => sel(x.Sample)).Where(x => x != null).Select(x => x!.Value).ToList();
        return v.Count > 0 ? v.Max() : null;
    }
}
