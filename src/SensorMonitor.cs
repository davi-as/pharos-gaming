using System.ComponentModel;

namespace PharosGaming;

public enum SensorType { Clock, Temperature, Load, Fan, Control, Power, Voltage, Current, Data, SmallData }

/// <summary>Linha do painel de sensores, com notificação de mudança para a interface.</summary>
public sealed class SensorRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public SensorType Type { get; init; }
    public string Category => CategoryName(Type);

    private float? _value, _min, _max;
    public float? RawValue => _value;
    public float? RawMax => _max;

    public string Value => Format(_value);
    public string Min => Format(_min);
    public string Max => Format(_max);

    public void Update(float? v)
    {
        _value = v;
        if (v is float x)
        {
            _min = _min is float a ? Math.Min(a, x) : x;
            _max = _max is float b ? Math.Max(b, x) : x;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Min)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Max)));
    }

    private string Format(float? v) => v is not float x ? "—" : Type switch
    {
        SensorType.Temperature => $"{x:0.#} °C",
        SensorType.Load or SensorType.Control => $"{x:0.#} %",
        SensorType.Clock => $"{x:0} MHz",
        SensorType.Power => $"{x:0.#} W",
        SensorType.Voltage => $"{x:0.###} V",
        SensorType.Current => $"{x:0.###} A",
        SensorType.Fan => $"{x:0} RPM",
        SensorType.Data => $"{x:0.##} GB",
        SensorType.SmallData => $"{x:0} MB",
        _ => $"{x:0.##}"
    };

    public static string CategoryName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatura",
        SensorType.Load => "Carga",
        SensorType.Clock => "Clock",
        SensorType.Power => "Consumo",
        SensorType.Voltage => "Tensão",
        SensorType.Current => "Corrente",
        SensorType.Fan => "Ventoinhas",
        SensorType.Control => "Controle",
        _ => "Dados",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Uma leitura bruta: id estável, hardware, nome, tipo e valor.</summary>
public readonly record struct SensorReading(string Id, string Hardware, string Name, SensorType Type, float? Value);

/// <summary>
/// Todos os sensores do Linux numa lista só, como o LibreHardwareMonitor fazia no Windows: chips hwmon (CPU, placa-mãe,
/// NVMe, rede, amdgpu), clock e carga por núcleo, NVML (NVIDIA) e memória. Guarda mínimo e máximo desde a abertura.
/// </summary>
public sealed class SensorMonitor
{
    public static SensorMonitor Shared { get; } = new();

    private readonly Dictionary<string, SensorRow> _byId = new();
    private (ulong Idle, ulong Total)[]? _prevStat;
    private readonly string _cpuName = SystemInfo.ParseCpuInfo(Sys.Read("/proc/cpuinfo") ?? "").Model;

    public List<SensorRow> Rows { get; } = new();

    /// <summary>Lê tudo e atualiza as linhas; sensores novos entram no fim (ex.: pendrive com sensor de temperatura).</summary>
    public void Refresh()
    {
        var readings = new List<SensorReading>();
        try { readings.AddRange(ReadHwmon("/sys/class/hwmon", _cpuName)); } catch { }
        try { readings.AddRange(ReadCpu()); } catch { }
        try { readings.AddRange(ReadNvidia()); } catch { }
        try { readings.AddRange(ReadMemory()); } catch { }
        foreach (var r in readings)
        {
            if (!_byId.TryGetValue(r.Id, out var row))
            {
                row = new SensorRow { Id = r.Id, Hardware = r.Hardware, Name = r.Name, Type = r.Type };
                _byId[r.Id] = row;
                Rows.Add(row);
            }
            row.Update(r.Value);
        }
    }

    // ---------------- hwmon ----------------

    /// <summary>Nome legível do chip hwmon ("k10temp" → CPU, "nct6687" → placa-mãe, "nvme" → modelo do SSD...).</summary>
    public static string HwmonHardware(string name, string dir, string cpuName)
    {
        string? model = Sys.Read(Path.Combine(dir, "device", "model")) ?? Sys.Read(Path.Combine(dir, "device", "device", "model"));
        return name switch
        {
            "k10temp" or "zenpower" or "coretemp" or "cpu_thermal" => cpuName.Length > 0 ? cpuName : "CPU",
            "nvme" => "NVMe · " + (model ?? "SSD"),
            "drivetemp" => "Disco · " + (model ?? "SATA"),
            "amdgpu" => "GPU AMD",
            "acpitz" => "ACPI (placa-mãe)",
            _ when name.StartsWith("nct") || name.StartsWith("it8") || name.StartsWith("asus") || name.StartsWith("f71")
                   || name.StartsWith("w83") || name.StartsWith("gigabyte") => $"Placa-mãe ({name})",
            _ when name.StartsWith("r8169") || name.StartsWith("igc") || name.StartsWith("e1000") || name.StartsWith("mt7") || name.StartsWith("iwlwifi") => $"Rede ({name})",
            "BAT0" or "BAT1" or "ADP1" or "AC" => "Bateria",
            _ => name,
        };
    }

    public static IEnumerable<SensorReading> ReadHwmon(string root, string cpuName)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Sys.Read(Path.Combine(dir, "name")) ?? Path.GetFileName(dir);
            string hw = HwmonHardware(name, dir, cpuName);
            string id0 = Path.GetFileName(dir);
            foreach (var input in Directory.GetFiles(dir, "*_input").Concat(Directory.GetFiles(dir, "power*_average")).Concat(Directory.GetFiles(dir, "pwm?"))
                                           .OrderBy(f => f, StringComparer.Ordinal))
            {
                string file = Path.GetFileName(input);
                string prefix = file.Split('_')[0];           // temp1, fan2, in0, power1, curr1, freq1, pwm1
                string kind = new string(prefix.TakeWhile(char.IsLetter).ToArray());
                long? raw = Sys.ReadLong(input);
                if (raw == null) continue;
                SensorType t;
                float value;
                switch (kind)
                {
                    case "temp": t = SensorType.Temperature; value = raw.Value / 1000f; break;   // m°C
                    case "fan": t = SensorType.Fan; value = raw.Value; break;                    // RPM
                    case "in": t = SensorType.Voltage; value = raw.Value / 1000f; break;         // mV
                    case "power": t = SensorType.Power; value = raw.Value / 1e6f; break;         // µW
                    case "curr": t = SensorType.Current; value = raw.Value / 1000f; break;       // mA
                    case "freq": t = SensorType.Clock; value = raw.Value / 1e6f; break;          // Hz
                    case "pwm": t = SensorType.Control; value = raw.Value * 100f / 255f; break;  // 0..255
                    default: continue;
                }
                if (t == SensorType.Temperature && value is <= -40 or >= 200) continue; // sensor desligado ou valor lixo
                string label = Sys.Read(Path.Combine(dir, prefix + "_label")) ?? DefaultLabel(kind, prefix);
                yield return new SensorReading($"{id0}/{file}", hw, label, t, value);
            }
        }
    }

    private static string DefaultLabel(string kind, string prefix)
    {
        string n = prefix[kind.Length..];
        return kind switch
        {
            "temp" => "Temperatura " + n, "fan" => "Ventoinha " + n, "in" => "Tensão " + n, "power" => "Consumo " + n,
            "curr" => "Corrente " + n, "freq" => "Clock " + n, "pwm" => "Controle PWM " + n, _ => prefix,
        };
    }

    // ---------------- CPU ----------------

    private IEnumerable<SensorReading> ReadCpu()
    {
        var cur = LinuxSensors.ParseProcStat(Sys.Read("/proc/stat") ?? "");
        var prev = _prevStat;
        _prevStat = cur;
        string hw = _cpuName.Length > 0 ? _cpuName : "CPU";
        if (prev != null && prev.Length == cur.Length && cur.Length > 1)
        {
            yield return new SensorReading("cpu/load", hw, "Carga total", SensorType.Load, LinuxSensors.Usage(prev[0], cur[0]));
            for (int i = 1; i < cur.Length; i++)
                yield return new SensorReading($"cpu/load{i - 1}", hw, $"Carga thread #{i - 1}", SensorType.Load, LinuxSensors.Usage(prev[i], cur[i]));
        }
        for (int i = 0; ; i++)
        {
            string f = $"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_cur_freq";
            if (!File.Exists(f)) { if (i > 0) break; else yield break; }
            if (Sys.ReadLong(f) is long khz) yield return new SensorReading($"cpu/clock{i}", hw, $"Clock thread #{i}", SensorType.Clock, khz / 1000f);
        }
    }

    // ---------------- NVIDIA ----------------

    private static IEnumerable<SensorReading> ReadNvidia()
    {
        foreach (var (h, pci) in Nvml.Devices())
        {
            string hw = Nvml.Name(h) ?? "GPU NVIDIA";
            string id = "nv" + pci.BusId;
            SensorReading R(string k, string n, SensorType t, float? v) => new($"{id}/{k}", hw, n, t, v);
            var u = Nvml.Util(h);
            yield return R("temp", "GPU", SensorType.Temperature, Nvml.Temperature(h));
            yield return R("load", "Carga do núcleo", SensorType.Load, u?.Gpu);
            yield return R("memload", "Controlador de memória", SensorType.Load, u?.Memory);
            yield return R("enc", "Codificador de vídeo", SensorType.Load, Nvml.EncoderUtil(h));
            yield return R("dec", "Decodificador de vídeo", SensorType.Load, Nvml.DecoderUtil(h));
            yield return R("clk", "Núcleo", SensorType.Clock, Nvml.Clock(h, Nvml.ClockGraphics));
            yield return R("memclk", "Memória", SensorType.Clock, Nvml.Clock(h, Nvml.ClockMem));
            yield return R("vidclk", "Vídeo", SensorType.Clock, Nvml.Clock(h, Nvml.ClockVideo));
            yield return R("power", "Placa", SensorType.Power, Nvml.PowerUsageMw(h) / 1000f);
            yield return R("fan", "Ventoinha", SensorType.Control, Nvml.FanSpeed(h));
            if (Nvml.MemoryInfo(h) is { } m)
            {
                yield return R("vramused", "Memória de vídeo usada", SensorType.SmallData, m.Used / 1048576f);
                yield return R("vramfree", "Memória de vídeo livre", SensorType.SmallData, m.Free / 1048576f);
                yield return R("vramload", "Memória de vídeo", SensorType.Load, m.Total > 0 ? 100f * m.Used / m.Total : null);
            }
        }
    }

    // ---------------- Memória ----------------

    private static IEnumerable<SensorReading> ReadMemory()
    {
        var m = LinuxSensors.ParseMeminfo(Sys.Read("/proc/meminfo") ?? "");
        double tot = m.GetValueOrDefault("MemTotal"), avail = m.GetValueOrDefault("MemAvailable");
        if (tot <= 0) yield break;
        const string hw = "Memória";
        yield return new SensorReading("mem/load", hw, "Memória", SensorType.Load, (float)(100 * (tot - avail) / tot));
        yield return new SensorReading("mem/used", hw, "Memória usada", SensorType.Data, (float)((tot - avail) / 1048576));
        yield return new SensorReading("mem/avail", hw, "Memória disponível", SensorType.Data, (float)(avail / 1048576));
        double st = m.GetValueOrDefault("SwapTotal"), sf = m.GetValueOrDefault("SwapFree");
        if (st > 0) yield return new SensorReading("mem/swap", hw, "Swap usado", SensorType.Data, (float)((st - sf) / 1048576));
    }
}
