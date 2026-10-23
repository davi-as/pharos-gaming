namespace PharosGaming;

public sealed record CpuReading(string Model, float? Total, float? MaxThread, int? MaxThreadIndex, float? Temp, float? ClockMhz, int Threads);
public sealed record GpuReading(string Name, float? Load, float? Temp, float? ClockMhz, float? PowerW, double? VramUsedMb,
                                double? VramTotalMb, float? Fan, bool ThermalCap)
{
    public double? VramPct => VramUsedMb is double u && VramTotalMb is > 0 ? 100 * u / VramTotalMb : null;
}
public sealed record MemReading(double UsedGb, double TotalGb, double SwapUsedGb)
{
    public float Pct => TotalGb > 0 ? (float)(100 * UsedGb / TotalGb) : 0;
}

/// <summary>
/// Sensores do Linux sem dependências: /proc (CPU e RAM), hwmon (temperatura), cpufreq (clock),
/// NVML (NVIDIA) e sysfs do amdgpu (AMD). Chame <see cref="Sample"/> em intervalo fixo: a carga é a diferença entre leituras.
/// </summary>
public sealed class LinuxSensors
{
    private (ulong Idle, ulong Total)[]? _prevStat;
    private readonly string _cpuModel = CpuModel();
    private readonly string? _cpuTempFile = FindCpuTempFile();
    private readonly IntPtr _nvml = Nvml.Devices().Select(d => d.Handle).FirstOrDefault();
    private readonly string? _amdDevice = FindAmdDevice();

    private static string? FindAmdDevice() =>
        Directory.Exists("/sys/class/drm")
            ? Directory.GetDirectories("/sys/class/drm", "card?").Select(c => Path.Combine(c, "device"))
                       .FirstOrDefault(d => Read(Path.Combine(d, "vendor")) == "0x1002" && File.Exists(Path.Combine(d, "gpu_busy_percent")))
            : null;

    public (CpuReading Cpu, GpuReading? Gpu, MemReading Mem) Sample() => (Cpu(), Gpu(), Mem());

    // ---------------- CPU ----------------

    private CpuReading Cpu()
    {
        var cur = ParseProcStat(Read("/proc/stat") ?? "");
        float? total = null, max = null;
        int? maxIdx = null;
        if (_prevStat is { } prev && prev.Length == cur.Length && cur.Length > 1)
        {
            total = Usage(prev[0], cur[0]);
            for (int i = 1; i < cur.Length; i++)
            {
                float u = Usage(prev[i], cur[i]);
                if (max == null || u > max) { max = u; maxIdx = i - 1; }
            }
        }
        _prevStat = cur;

        var freqs = Directory.Exists("/sys/devices/system/cpu")
            ? Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
                       .Select(d => Read(Path.Combine(d, "cpufreq/scaling_cur_freq")))
                       .Where(s => s != null).Select(s => double.Parse(s!) / 1000).ToList()
            : new List<double>();

        return new CpuReading(_cpuModel, total, max, maxIdx, MilliToUnit(_cpuTempFile),
                              freqs.Count > 0 ? (float)freqs.Average() : null, Math.Max(0, cur.Length - 1));
    }

    /// <summary>Linhas "cpu" e "cpuN" do /proc/stat → (ocioso, total) em jiffies. Índice 0 é o agregado.</summary>
    internal static (ulong Idle, ulong Total)[] ParseProcStat(string stat) =>
        stat.Split('\n').Where(l => l.StartsWith("cpu")).Select(l =>
        {
            var v = l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(ulong.Parse).ToArray();
            return (v[3] + v[4], v.Aggregate(0UL, (a, b) => a + b)); // idle + iowait; user..steal
        }).ToArray();

    internal static float Usage((ulong Idle, ulong Total) a, (ulong Idle, ulong Total) b)
    {
        double dt = b.Total - a.Total;
        return dt <= 0 ? 0 : (float)Math.Clamp(100 * (1 - (b.Idle - a.Idle) / dt), 0, 100);
    }

    private static string CpuModel() =>
        Read("/proc/cpuinfo")?.Split('\n').FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "CPU";

    /// <summary>Tctl/Tdie (AMD, k10temp/zenpower) ou Package (Intel, coretemp).</summary>
    private static string? FindCpuTempFile()
    {
        if (!Directory.Exists("/sys/class/hwmon")) return null;
        foreach (var hw in Directory.GetDirectories("/sys/class/hwmon"))
        {
            string? name = Read(Path.Combine(hw, "name"));
            if (name is not ("k10temp" or "zenpower" or "coretemp")) continue;
            var labeled = Directory.GetFiles(hw, "temp*_label").FirstOrDefault(f =>
                Read(f) is string l && (l is "Tctl" or "Tdie" || l.StartsWith("Package")));
            var file = labeled?.Replace("_label", "_input") ?? Path.Combine(hw, "temp1_input");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    // ---------------- GPU ----------------

    private GpuReading? Gpu()
    {
        if (_nvml != IntPtr.Zero)
        {
            var mem = Nvml.MemoryInfo(_nvml);
            return new GpuReading(Nvml.Name(_nvml) ?? "GPU NVIDIA", Nvml.Util(_nvml)?.Gpu, Nvml.Temperature(_nvml),
                                  Nvml.Clock(_nvml, Nvml.ClockGraphics), Nvml.PowerUsageMw(_nvml) / 1000f,
                                  mem?.Used / 1048576.0, mem?.Total / 1048576.0, Nvml.FanSpeed(_nvml),
                                  (Nvml.ThrottleReasons(_nvml) & 0x60) is > 0); // SW/HW thermal slowdown
        }
        if (_amdDevice is string d)
        {
            string? hw = Directory.Exists(Path.Combine(d, "hwmon")) ? Directory.GetDirectories(Path.Combine(d, "hwmon")).FirstOrDefault() : null;
            float? Hw(string f, double div) => hw != null && Read(Path.Combine(hw, f)) is string s ? (float)(double.Parse(s) / div) : null;
            double? Bytes(string f) => Read(Path.Combine(d, f)) is string s ? double.Parse(s) / 1048576 : null;
            // shortcut: sem nome comercial nem detecção de limite térmico na AMD; ler pci.ids / pp_features quando houver AMD para testar
            return new GpuReading("GPU AMD", Read(Path.Combine(d, "gpu_busy_percent")) is string b ? float.Parse(b) : null,
                                  Hw("temp1_input", 1000), Hw("freq1_input", 1e6), Hw("power1_average", 1e6) ?? Hw("power1_input", 1e6),
                                  Bytes("mem_info_vram_used"), Bytes("mem_info_vram_total"), null, false);
        }
        return null;
    }

    // ---------------- RAM ----------------

    private static MemReading Mem()
    {
        var m = ParseMeminfo(Read("/proc/meminfo") ?? "");
        double Kb(string k) => m.TryGetValue(k, out var v) ? v : 0;
        const double Gb = 1024 * 1024;
        return new MemReading((Kb("MemTotal") - Kb("MemAvailable")) / Gb, Kb("MemTotal") / Gb, (Kb("SwapTotal") - Kb("SwapFree")) / Gb);
    }

    internal static Dictionary<string, double> ParseMeminfo(string text) =>
        text.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => double.Parse(p[1].Trim().Split(' ')[0]));

    // ---------------- util ----------------

    private static float? MilliToUnit(string? file) => file != null && Read(file) is string s ? float.Parse(s) / 1000 : null;

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
