using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>Módulo de RAM como o udev o descreve (MEMORY_DEVICE_N_*), lido da tabela SMBIOS sem precisar de root.</summary>
public sealed record RamModule(string Locator, string Bank, long Size, string Type, string Form, int Rated, int Configured,
                               string Maker, string Part, string Serial, int DataWidth, int TotalWidth, int Rank);

/// <summary>Disco ou partição do lsblk (árvore: disco → partição → LUKS/LVM → sistema de arquivos).</summary>
public sealed record BlockDev(string Name, string Type, string? Model, string? Serial, string? Rev, string? Tran, long Size, bool Rotational,
                              bool Removable, string? FsType, string? Label, List<string> Mounts, List<BlockDev> Children);

/// <summary>Placa de vídeo do lspci (endereço PCI, fabricante e modelo).</summary>
public sealed record PciDisplay(string Slot, string Vendor, string VendorId, string Device, string DeviceId, string Class);

/// <summary>
/// Informações estáticas do sistema: /etc/os-release, /proc, sysfs (DMI, cpufreq, caches), udev (módulos de RAM),
/// lspci e lsblk. Nada exige root. (No PHAROS para Windows vinha do WMI.)
/// </summary>
public static class SystemInfo
{
    public static List<InfoNode> Collect()
    {
        var roots = new List<InfoNode>();
        Safe(roots, "Sistema operacional", CollectOs);
        Safe(roots, "Placa-mãe e BIOS", CollectBoard);
        Safe(roots, "Processador", CollectCpu);
        Safe(roots, "Memória RAM", CollectRam);
        Safe(roots, "Placa de vídeo", CollectGpu);
        Safe(roots, "Armazenamento", CollectDisks);
        return roots;
    }

    private static void Safe(List<InfoNode> roots, string title, Action<InfoNode> fill)
    {
        var node = new InfoNode { Header = title };
        try { fill(node); }
        catch (Exception ex) { node.Add("Erro ao ler", ex.Message); }
        roots.Add(node);
    }

    private static string Or(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Trim();

    // ---------------- Sistema operacional ----------------

    public static Dictionary<string, string> ParseKeyValues(string text, char sep = '=')
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(sep);
            if (i <= 0) continue;
            d[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"');
        }
        return d;
    }

    private static void CollectOs(InfoNode n)
    {
        var os = ParseKeyValues(Sys.Read("/etc/os-release") ?? Sys.Read("/usr/lib/os-release") ?? "");
        n.Add("Distribuição", Or(os.GetValueOrDefault("PRETTY_NAME") ?? os.GetValueOrDefault("NAME")));
        n.Add("Kernel", Or(Sys.Read("/proc/sys/kernel/osrelease")));
        n.Add("Arquitetura", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        n.Add("Nome do computador", Or(Sys.Read("/proc/sys/kernel/hostname")));
        string desk = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION") ?? "";
        string session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
        n.Add("Ambiente gráfico", Or(string.Join(" · ", new[] { desk, session }.Where(s => s.Length > 0))));
        if (double.TryParse((Sys.Read("/proc/uptime") ?? "").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out double up))
        {
            var t = TimeSpan.FromSeconds(up);
            n.Add("Ligado há", t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : $"{t.Hours} h {t.Minutes} min");
        }
    }

    // ---------------- Placa-mãe ----------------

    private static string? Dmi(string f) => Sys.Read("/sys/class/dmi/id/" + f) is { Length: > 0 } v && !v.Contains("O.E.M.") ? v : null;

    private static void CollectBoard(InfoNode n)
    {
        var board = n.Add("Placa-mãe");
        board.Add("Fabricante", Or(Dmi("board_vendor")));
        board.Add("Modelo", Or(Dmi("board_name")));
        board.Add("Versão", Or(Dmi("board_version")));
        if (Dmi("board_serial") is string serial) board.Add("Nº de série", serial); // só root lê, na maioria das distros
        var bios = n.Add("BIOS");
        bios.Add("Fabricante", Or(Dmi("bios_vendor")));
        bios.Add("Versão", Or(Dmi("bios_version")));
        bios.Add("Data", Or(Dmi("bios_date")));
        bios.Add("Modo de boot", Directory.Exists("/sys/firmware/efi") ? "UEFI" : "Legado (BIOS)");
        if (Dmi("sys_vendor") is string sv && Dmi("product_name") is string pn && pn != Dmi("board_name"))
            n.Add("Computador", $"{sv} {pn}");
    }

    // ---------------- Processador ----------------

    public sealed record CpuInfo(string Model, string Vendor, int Threads, int Cores, int Sockets, HashSet<string> Flags);

    public static CpuInfo ParseCpuInfo(string text)
    {
        string model = "", vendor = "";
        int threads = 0;
        var cores = new HashSet<(string, string)>();
        var sockets = new HashSet<string>();
        var flags = new HashSet<string>();
        string phys = "0";
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = ParseKeyValues(block, ':');
            if (!kv.ContainsKey("processor")) continue;
            threads++;
            model = kv.GetValueOrDefault("model name", model);
            vendor = kv.GetValueOrDefault("vendor_id", vendor);
            phys = kv.GetValueOrDefault("physical id", "0");
            sockets.Add(phys);
            if (kv.TryGetValue("core id", out var core)) cores.Add((phys, core));
            if (flags.Count == 0 && kv.TryGetValue("flags", out var f)) flags.UnionWith(f.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return new CpuInfo(model, vendor, threads, cores.Count > 0 ? cores.Count : threads, Math.Max(1, sockets.Count), flags);
    }

    private static void CollectCpu(InfoNode n)
    {
        var c = ParseCpuInfo(Sys.Read("/proc/cpuinfo") ?? "");
        var cpu = n.Add(Or(c.Model));
        cpu.Add("Fabricante", c.Vendor switch { "AuthenticAMD" => "AMD", "GenuineIntel" => "Intel", var v => Or(v) });
        cpu.Add("Núcleos físicos", c.Cores.ToString());
        cpu.Add("Threads (lógicos)", c.Threads.ToString());
        if (c.Sockets > 1) cpu.Add("Soquetes", c.Sockets.ToString());
        const string fq = "/sys/devices/system/cpu/cpu0/cpufreq/";
        if (Sys.ReadLong(fq + "cpuinfo_max_freq") is long max) cpu.Add("Clock máximo", $"{max / 1000} MHz");
        foreach (var (level, size) in Caches()) cpu.Add($"Cache {level}", size);
        cpu.Add("Virtualização", c.Flags.Contains("svm") ? "AMD-V disponível" : c.Flags.Contains("vmx") ? "Intel VT-x disponível" : "Desativada na BIOS ou ausente");
        if (Sys.Read(fq + "scaling_driver") is string drv) cpu.Add("Driver de frequência", drv);
        if (Sys.Read(fq + "scaling_governor") is string gov) cpu.Add("Governador", gov);
        if (Sys.Read(fq + "energy_performance_preference") is string epp) cpu.Add("Preferência de energia (EPP)", epp);
        string? boost = Sys.Read("/sys/devices/system/cpu/cpufreq/boost")
                        ?? (Sys.Read("/sys/devices/system/cpu/intel_pstate/no_turbo") is string nt ? (nt == "1" ? "0" : "1") : null);
        if (boost != null) cpu.Add("Boost / Turbo", boost == "1" ? "Ligado" : "Desligado");
        var isa = new[] { ("avx2", "AVX2"), ("avx512f", "AVX-512"), ("sha_ni", "SHA"), ("aes", "AES-NI") }.Where(x => c.Flags.Contains(x.Item1)).Select(x => x.Item2);
        cpu.Add("Instruções", Or(string.Join(", ", isa)));
    }

    /// <summary>Tamanho total por nível de cache (L1d + L1i, L2, L3), somando cada instância compartilhada uma vez só.</summary>
    private static IEnumerable<(string Level, string Size)> Caches()
    {
        var seen = new HashSet<string>();
        var kb = new SortedDictionary<string, long>();
        foreach (var cpuDir in Directory.Exists("/sys/devices/system/cpu") ? Directory.GetDirectories("/sys/devices/system/cpu", "cpu*") : [])
        {
            string cache = Path.Combine(cpuDir, "cache");
            if (!Directory.Exists(cache)) continue;
            foreach (var idx in Directory.GetDirectories(cache, "index*"))
            {
                string? level = Sys.Read(Path.Combine(idx, "level")), type = Sys.Read(Path.Combine(idx, "type")),
                        size = Sys.Read(Path.Combine(idx, "size")), shared = Sys.Read(Path.Combine(idx, "shared_cpu_list"));
                if (level == null || size == null || !seen.Add($"{level}{type}{shared}")) continue;
                string key = level == "1" ? (type == "Instruction" ? "L1 instruções" : "L1 dados") : "L" + level;
                long v = long.TryParse(Regex.Match(size, @"\d+").Value, out long x) ? x : 0;
                if (size.EndsWith("M")) v *= 1024;
                kb[key] = kb.GetValueOrDefault(key) + v;
            }
        }
        return kb.Select(k => (k.Key, k.Value >= 1024 ? $"{k.Value / 1024.0:0.#} MB" : $"{k.Value} KB"));
    }

    // ---------------- Memória RAM ----------------

    public static (List<RamModule> Modules, int Slots, long MaxBytes) ParseUdevMemory(string props)
    {
        var kv = ParseKeyValues(props);
        var mods = new List<RamModule>();
        for (int i = 0; ; i++)
        {
            string p = $"MEMORY_DEVICE_{i}_";
            if (!kv.Keys.Any(k => k.StartsWith(p))) break;
            string S(string k) => kv.GetValueOrDefault(p + k, "").Trim();
            int I(string k) => int.TryParse(S(k), out int v) ? v : 0;
            long size = long.TryParse(S("SIZE"), out long sz) ? sz : 0;
            if (size <= 0) continue; // slot vazio
            mods.Add(new RamModule(S("LOCATOR"), S("BANK_LOCATOR"), size, S("TYPE"), S("FORM_FACTOR"), I("SPEED_MTS"), I("CONFIGURED_SPEED_MTS"),
                                   S("MANUFACTURER"), S("PART_NUMBER"), S("SERIAL_NUMBER"), I("DATA_WIDTH"), I("TOTAL_WIDTH"), I("RANK")));
        }
        int slots = int.TryParse(kv.GetValueOrDefault("MEMORY_ARRAY_NUM_DEVICES"), out int n) ? n : 0;
        if (slots == 0) slots = kv.Keys.Count(k => Regex.IsMatch(k, @"^MEMORY_DEVICE_\d+_LOCATOR$"));
        long max = long.TryParse(kv.GetValueOrDefault("MEMORY_ARRAY_MAX_CAPACITY"), out long m) ? m : 0;
        return (mods, slots, max);
    }

    // Velocidades que costumam aparecer no part number do módulo (ex.: CMK32GX5M2B6000C30, F5-6000J3038F16GX2, KF3200C16D4/8GX).
    private static readonly Regex SpeedInPart = new(
        @"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|5800|6000|6200|6400|6600|6800|7000|7200|7400|7600|7800|8000|8200|8400|8800)(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex ChannelInLocator = new(
        @"(?i)(?:channel|chan|ch)[\s_-]*([A-H0-9])|dimm[\s_-]*([A-H])\d|(?:^|[^A-Za-z])([A-H])[12](?:$|[^0-9])", RegexOptions.Compiled);

    public static string? ChannelOf(string locator)
    {
        var m = ChannelInLocator.Match(locator);
        if (!m.Success) return null;
        for (int g = 1; g <= 3; g++) if (m.Groups[g].Success) return m.Groups[g].Value.ToUpperInvariant();
        return null;
    }

    /// <summary>EXPO/XMP estimado pela velocidade escrita no part number (o sistema não diz se o perfil está ligado).</summary>
    public static string ProfileEstimate(IReadOnlyList<RamModule> mods)
    {
        int conf = mods.Where(m => m.Configured > 0).Select(m => m.Configured).DefaultIfEmpty(0).Min();
        int rated = mods.Select(m => m.Rated).DefaultIfEmpty(0).Max();
        int label = mods.Select(m => SpeedInPart.Match(m.Part)).Where(x => x.Success).Select(x => int.Parse(x.Value)).DefaultIfEmpty(0).Max();
        if (conf <= 0) return "Não foi possível ler a velocidade em uso.";
        if (label > 0 && conf < label * 0.95)
            return $"Provavelmente DESLIGADO: a RAM roda a {conf} MT/s, mas o part number indica {label} MT/s. Ative EXPO/XMP na BIOS.";
        if (label > 0) return $"Provavelmente ATIVO: roda a {conf} MT/s (part number indica {label} MT/s).";
        if (rated > 0 && conf < rated) return $"Roda abaixo da velocidade nominal ({conf} de {rated} MT/s). Confira EXPO/XMP na BIOS.";
        return "Sem como estimar (o part number não traz a velocidade). Confira na BIOS.";
    }

    public static string ChannelEstimate(IReadOnlyList<RamModule> mods)
    {
        if (mods.Count == 1) return "Canal único (1 módulo). Com 2 módulos iguais a RAM roda em dual channel e fica bem mais rápida.";
        var chans = mods.Select(m => ChannelOf(m.Bank) ?? ChannelOf(m.Locator)).ToList();
        if (chans.All(c => c != null))
        {
            int distinct = chans.Distinct().Count();
            return distinct >= 2 ? $"{distinct} canais (dual channel ou mais)"
                : "Canal único: os módulos estão no mesmo canal. Mude para os slots recomendados no manual da placa.";
        }
        return $"Não deu para identificar pelos nomes dos slots ({string.Join(", ", mods.Select(m => m.Locator))}). Veja o manual da placa.";
    }

    private static void CollectRam(InfoNode n)
    {
        var mem = LinuxSensors.ParseMeminfo(Sys.Read("/proc/meminfo") ?? "");
        var r = Sys.RunAsync("udevadm", "info", "-q", "property", "-p", "/sys/devices/virtual/dmi/id").GetAwaiter().GetResult();
        var (mods, slots, maxBytes) = ParseUdevMemory(r.Out);

        long total = mods.Sum(m => m.Size);
        n.Add("Total instalado", total > 0 ? Sys.Bytes(total) : Sys.Bytes(mem.GetValueOrDefault("MemTotal") * 1024.0));
        var sum = n.Add("Resumo");
        sum.Add("Módulos instalados", slots > 0 ? $"{mods.Count} de {slots} slots" : mods.Count.ToString());
        if (maxBytes > 0) sum.Add("Capacidade máxima suportada pela placa", Sys.Bytes(maxBytes));
        if (mods.Count > 0)
        {
            sum.Add("Tipo", string.Join(" / ", mods.Select(m => m.Type).Distinct()));
            int conf = mods.Where(m => m.Configured > 0).Select(m => m.Configured).DefaultIfEmpty(0).Min();
            int rated = mods.Select(m => m.Rated).DefaultIfEmpty(0).Max();
            sum.Add("Velocidade em uso", conf > 0 ? $"{conf} MT/s" : "—");
            sum.Add("Velocidade nominal reportada pelo módulo", rated > 0 ? $"{rated} MT/s" : "—");
            sum.Add("Perfil EXPO/XMP (estimado)", ProfileEstimate(mods));
            sum.Add("Canais (estimado)", ChannelEstimate(mods));
            sum.Add("ECC", mods.Any(m => m.TotalWidth > m.DataWidth && m.DataWidth > 0) ? "Sim (correção de erros)" : "Não");
        }
        else sum.Add("Módulos", "O udev não informou os módulos (tabela SMBIOS ausente, comum em máquinas virtuais).");
        sum.Add("Timings (CL, tRCD, tRP...)", "O sistema não expõe. Veja na BIOS.");
        double totKb = mem.GetValueOrDefault("MemTotal"), availKb = mem.GetValueOrDefault("MemAvailable");
        if (totKb > 0) sum.Add("Em uso (no momento da leitura)", $"{Sys.Bytes((totKb - availKb) * 1024.0)} de {Sys.Bytes(totKb * 1024.0)} ({(totKb - availKb) / totKb:P0})");
        double swapT = mem.GetValueOrDefault("SwapTotal"), swapF = mem.GetValueOrDefault("SwapFree");
        sum.Add("Swap em uso", swapT > 0 ? $"{Sys.Bytes((swapT - swapF) * 1024.0)} de {Sys.Bytes(swapT * 1024.0)}" : "Sem swap");

        foreach (var m in mods)
        {
            var slot = n.Add($"Módulo — {m.Bank} / {m.Locator}");
            slot.Add("Capacidade", Sys.Bytes(m.Size));
            slot.Add("Tipo", Or(m.Type));
            slot.Add("Formato", Or(m.Form));
            slot.Add("Ranks", m.Rank switch { 1 => "1 (single)", 2 => "2 (dual)", 4 => "4 (quad)", 0 => "—", var x => x.ToString() });
            slot.Add("Velocidade em uso", m.Configured > 0 ? $"{m.Configured} MT/s" : "—");
            slot.Add("Velocidade nominal", m.Rated > 0 ? $"{m.Rated} MT/s" : "—");
            slot.Add("Largura de dados / total", m.DataWidth > 0 ? $"{m.DataWidth} / {m.TotalWidth} bits" : "—");
            slot.Add("Fabricante", Or(m.Maker));
            slot.Add("Part number", Or(m.Part));
            slot.Add("Nº de série", Or(m.Serial));
        }
    }

    // ---------------- Placa de vídeo ----------------

    private static readonly Regex Quoted = new("\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>Linhas do "lspci -mm -nn -D" que são placas de vídeo (classe 03xx).</summary>
    public static List<PciDisplay> ParseLspci(string text)
    {
        var list = new List<PciDisplay>();
        foreach (var line in text.Split('\n'))
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var q = Quoted.Matches(line).Select(m => m.Groups[1].Value).ToList();
            if (q.Count < 3 || !Regex.IsMatch(q[0], @"\[03[0-9a-f]{2}\]$")) continue;
            static (string Name, string Id) Split(string s)
            {
                var m = Regex.Match(s, @"^(.*?)\s*\[([0-9a-f]{4})\]$");
                return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (s, "");
            }
            var (vendor, vid) = Split(q[1]);
            var (device, did) = Split(q[2]);
            list.Add(new PciDisplay(line[..sp], vendor, vid, device, did, Split(q[0]).Name));
        }
        return list;
    }

    /// <summary>Nome comercial: o que está entre colchetes no lspci ("GA106 [GeForce RTX 3060 Lite Hash Rate]" → "GeForce RTX 3060 Lite Hash Rate").</summary>
    public static string MarketingName(PciDisplay g)
    {
        var m = Regex.Match(g.Device, @"\[([^\]]+)\]");
        string vendor = g.VendorId switch { "10de" => "NVIDIA", "1002" => "AMD", "8086" => "Intel", _ => g.Vendor };
        return $"{vendor} {(m.Success ? m.Groups[1].Value : g.Device)}";
    }

    public static string? PciDriver(string slot) =>
        new DirectoryInfo($"/sys/bus/pci/devices/{slot}/driver").ResolveLinkTarget(false)?.Name;

    private static void CollectGpu(InfoNode n)
    {
        var gpus = ParseLspci(Sys.RunAsync("lspci", "-mm", "-nn", "-D").GetAwaiter().GetResult().Out);
        foreach (var g in gpus)
        {
            var node = n.Add(MarketingName(g));
            node.Add("Chip", g.Device);
            node.Add("Endereço PCI", g.Slot);
            node.Add("Driver em uso", Or(PciDriver(g.Slot)));
            if (g.VendorId == "10de" && Nvml.DriverVersion() is string dv) node.Add("Versão do driver NVIDIA", dv);
            string dev = $"/sys/bus/pci/devices/{g.Slot}/";
            if (Sys.ReadLong(dev + "mem_info_vram_total") is long vram) node.Add("Memória de vídeo", Sys.Bytes(vram));
            if (Sys.Read(dev + "current_link_speed") is string ls && Sys.Read(dev + "current_link_width") is string lw)
                node.Add("Link PCIe atual", $"x{lw} · {ls}");
        }
        if (gpus.Count == 0) n.Add("Placa de vídeo", "Nenhuma detectada pelo lspci.");
        var mon = n.Add("Monitores");
        foreach (var (name, mode) in Monitors()) mon.Add(name, mode);
    }

    /// <summary>Monitores conectados: no Hyprland com a taxa atual; nos outros, a resolução preferida de cada saída (DRM).</summary>
    private static List<(string Name, string Mode)> Monitors()
    {
        var list = new List<(string, string)>();
        if (Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(Sys.RunAsync("hyprctl", "monitors", "-j").GetAwaiter().GetResult().Out);
                foreach (var m in doc.RootElement.EnumerateArray())
                    list.Add(($"{m.GetProperty("name").GetString()} · {m.GetProperty("description").GetString()}",
                              $"{m.GetProperty("width").GetInt32()}x{m.GetProperty("height").GetInt32()} @ {m.GetProperty("refreshRate").GetDouble():0.#} Hz"
                              + (m.TryGetProperty("vrr", out var vrr) && vrr.GetBoolean() ? " · VRR" : "")));
                if (list.Count > 0) return list;
            }
            catch { }
        }
        foreach (var c in Directory.Exists("/sys/class/drm") ? Directory.GetDirectories("/sys/class/drm", "card*-*") : [])
            if (Sys.Read(Path.Combine(c, "status")) == "connected")
                list.Add((Path.GetFileName(c)[(Path.GetFileName(c).IndexOf('-') + 1)..],
                          (Sys.Read(Path.Combine(c, "modes")) ?? "").Split('\n')[0] is { Length: > 0 } mode ? mode + " (preferida)" : "—"));
        return list;
    }

    // ---------------- Armazenamento ----------------

    public static List<BlockDev> ParseLsblk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("blockdevices").EnumerateArray().Select(Dev).ToList();

        static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        // lsblk antigo grava booleanos como "0"/"1"
        static bool B(JsonElement e, string k) => e.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "1"));
        static long L(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out long x) ? x : 0 : 0;
        static BlockDev Dev(JsonElement e)
        {
            var mounts = new List<string>();
            if (e.TryGetProperty("mountpoints", out var mp) && mp.ValueKind == JsonValueKind.Array)
                mounts.AddRange(mp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            else if (S(e, "mountpoint") is string one) mounts.Add(one);
            var kids = e.TryGetProperty("children", out var c) ? c.EnumerateArray().Select(Dev).ToList() : new List<BlockDev>();
            return new BlockDev(S(e, "name") ?? "", S(e, "type") ?? "", S(e, "model"), S(e, "serial"), S(e, "rev"), S(e, "tran"), L(e, "size"),
                                B(e, "rota"), B(e, "rm") || B(e, "hotplug"), S(e, "fstype"), S(e, "label"), mounts, kids);
        }
    }

    public const string LsblkColumns = "NAME,MODEL,SERIAL,REV,TRAN,SIZE,ROTA,RM,HOTPLUG,FSTYPE,LABEL,MOUNTPOINTS,TYPE";

    public static List<BlockDev> ReadBlockDevices() =>
        ParseLsblk(Sys.RunAsync("lsblk", "-J", "-b", "-o", LsblkColumns).GetAwaiter().GetResult().Out is { Length: > 0 } j ? j : "{\"blockdevices\":[]}");

    public static string Kind(BlockDev d) =>
        d.Tran == "nvme" || d.Name.StartsWith("nvme") ? "SSD NVMe" : d.Tran == "usb" ? "USB" : d.Rotational ? "HD" : "SSD";

    private static void CollectDisks(InfoNode n)
    {
        foreach (var d in ReadBlockDevices().Where(d => d.Type == "disk" && d.Size > 0 && !d.Name.StartsWith("zram")))
        {
            var disk = n.Add(d.Model ?? d.Name);
            disk.Add("Capacidade", Sys.Bytes(d.Size, si: true));
            disk.Add("Tipo", Kind(d));
            disk.Add("Interface", Or(d.Tran?.ToUpperInvariant()));
            disk.Add("Dispositivo", "/dev/" + d.Name);
            disk.Add("Nº de série", Or(d.Serial));
            disk.Add("Firmware", Or(d.Rev));
        }
    }
}
