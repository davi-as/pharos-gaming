using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>Uma placa de vídeo com os dados no estilo da aba "Graphics Card" do GPU-Z.</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string Subtitle { get; set; } = "";
    public string Vendor { get; init; } = "";
    public string Slot { get; init; } = "";
    /// <summary>Nome do hardware desta placa na página Sensores (para mostrar os sensores ao vivo dela).</summary>
    public string SensorHardware { get; init; } = "";
    public List<InfoNode> Sections { get; } = new();
    internal IntPtr NvmlHandle { get; init; }
    internal ulong VramBytes { get; set; }

    /// <summary>Leituras dinâmicas que a lista de sensores não traz (P-state, limitador, link PCIe...).</summary>
    public List<KeyValuePair<string, string>> LiveExtras()
    {
        var list = new List<KeyValuePair<string, string>>();
        var h = NvmlHandle;
        string dev = $"/sys/bus/pci/devices/{Slot}/";
        if (h != IntPtr.Zero)
        {
            if (Nvml.PState(h) is int p) list.Add(new("Estado de desempenho", $"P{p}"));
            if (Nvml.ThrottleReasons(h) is ulong r) list.Add(new("PerfCap (limitador)", PerfCap(r)));
            if (Nvml.PowerUsageMw(h) is uint pw && Nvml.PowerLimitMw(h) is uint lim && lim > 0)
                list.Add(new("Consumo / limite", $"{pw / 1000.0:0.0} W · {100.0 * pw / lim:0} %"));
            if (Nvml.CurPcieGen(h) is uint g && Nvml.CurPcieWidth(h) is uint w) list.Add(new("PCIe em uso", $"{PcieName(g)} x{w}"));
        }
        else
        {
            if (Sys.Read(dev + "power_dpm_force_performance_level") is string lvl) list.Add(new("Nível de desempenho", lvl));
            if (Sys.Read(dev + "gpu_busy_percent") is string busy) list.Add(new("Carga", busy + " %"));
            if (PcieGen(Sys.Read(dev + "current_link_speed")) is uint g && Sys.Read(dev + "current_link_width") is string w)
                list.Add(new("PCIe em uso", $"{PcieName(g)} x{w}"));
        }
        return list;
    }

    private static string PerfCap(ulong r)
    {
        var parts = new List<string>();
        if ((r & 0x4) != 0) parts.Add("Energia");
        if ((r & 0x60) != 0) parts.Add("Térmico");
        if ((r & 0x8) != 0) parts.Add("HW slowdown");
        if ((r & 0x80) != 0) parts.Add("Power brake");
        if (parts.Count == 0) return (r & 0x1) != 0 ? "Ociosa" : "Nenhum";
        return string.Join(" + ", parts);
    }

    internal static string PcieName(uint gen) => gen switch
    {
        1 => "PCIe 1.1", 2 => "PCIe 2.0", 3 => "PCIe 3.0", 4 => "PCIe 4.0", 5 => "PCIe 5.0", 6 => "PCIe 6.0",
        _ => $"PCIe gen {gen}"
    };

    /// <summary>"16.0 GT/s PCIe" (sysfs) → geração 4.</summary>
    internal static uint? PcieGen(string? speed) =>
        double.TryParse(Regex.Match(speed ?? "", @"[\d.]+").Value, System.Globalization.CultureInfo.InvariantCulture, out double gts)
            ? gts switch { >= 64 => 6, >= 32 => 5, >= 16 => 4, >= 8 => 3, >= 5 => 2, > 0 => 1, _ => null }
            : null;
}

/// <summary>Placas de vídeo pelo lspci + sysfs (PCI, amdgpu) + NVML, com o banco de especificações do GPU-Z.</summary>
public static class GpuInfoCollector
{
    public static List<GpuInfo> Collect()
    {
        var result = new List<GpuInfo>();
        var nvDevices = Nvml.Devices();
        foreach (var pci in SystemInfo.ParseLspci(Sys.RunAsync("lspci", "-mm", "-nn", "-D").GetAwaiter().GetResult().Out))
        {
            try { result.Add(Build(pci, nvDevices)); }
            catch (Exception ex)
            {
                var g = new GpuInfo { Name = SystemInfo.MarketingName(pci), Slot = pci.Slot };
                var n = new InfoNode { Header = "ERRO" };
                n.Add("Falha ao ler", ex.Message);
                g.Sections.Add(n);
                result.Add(g);
            }
        }
        // Placa dedicada primeiro (mais VRAM).
        return result.OrderByDescending(g => g.VramBytes).ToList();
    }

    /// <summary>Maior BAR do arquivo "resource" do sysfs (linhas "início fim flags" em hexadecimal).</summary>
    public static ulong LargestBar(string resource)
    {
        ulong max = 0;
        foreach (var line in resource.Split('\n'))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            ulong a = Convert.ToUInt64(p[0], 16), b = Convert.ToUInt64(p[1], 16);
            if (b > a) max = Math.Max(max, b - a + 1);
        }
        return max;
    }

    private static string Hex(string? v) => (v ?? "").Replace("0x", "").ToUpperInvariant();

    private static GpuInfo Build(PciDisplay pci, List<(IntPtr Handle, Nvml.PciInfo Pci)> nvDevices)
    {
        string dev = $"/sys/bus/pci/devices/{pci.Slot}/";
        string ven = Hex(Sys.Read(dev + "vendor")) is { Length: 4 } v ? v : pci.VendorId.ToUpperInvariant();
        string did = Hex(Sys.Read(dev + "device")) is { Length: 4 } d ? d : pci.DeviceId.ToUpperInvariant();
        string subVen = Hex(Sys.Read(dev + "subsystem_vendor")), subDev = Hex(Sys.Read(dev + "subsystem_device"));
        string rev = Hex(Sys.Read(dev + "revision"));
        string vendor = ven switch { "10DE" => "NVIDIA", "1002" => "AMD", "8086" => "Intel", _ => pci.Vendor };

        IntPtr h = IntPtr.Zero;
        if (ven == "10DE")
        {
            uint pciId = (Convert.ToUInt32(did, 16) << 16) | 0x10DE;
            h = nvDevices.FirstOrDefault(n => n.Pci.PciDeviceId == pciId).Handle;
        }
        string name = (h != IntPtr.Zero ? Nvml.Name(h) : null) ?? SystemInfo.MarketingName(pci);
        if (!name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) name = $"{vendor} {name}";
        var spec = GpuSpecDb.Find(ven, did, name);
        string? driver = SystemInfo.PciDriver(pci.Slot);

        var gpu = new GpuInfo
        {
            Name = name, Vendor = vendor, Slot = pci.Slot, NvmlHandle = h,
            SensorHardware = h != IntPtr.Zero ? Nvml.Name(h) ?? "GPU NVIDIA" : driver == "amdgpu" ? "GPU AMD" : name,
        };

        // ---------- Identificação ----------
        var id = Section(gpu, "IDENTIFICAÇÃO");
        id.Add("Nome", name);
        id.Add("Chip", spec?.Chip ?? Regex.Match(pci.Device, @"^\S+").Value);
        id.Add("Arquitetura", spec?.Arch ?? "—");
        id.Add("Revisão", rev.Length > 0 ? rev : "—");
        id.Add("Fabricante da placa", subVen.Length > 0 ? GpuSpecDb.SubvendorName(subVen) : "—");
        id.Add("Device ID", $"{ven} {did} · {subVen} {subDev}".Trim(' ', '·'));
        id.Add("Processo", spec?.Process ?? "—");
        id.Add("Área do die", spec != null ? $"{spec.DieMm2} mm²" : "—");
        id.Add("Transistores", spec != null ? $"{spec.TransistorsBi:0.#} bilhões" : "—");
        id.Add("Lançamento", spec?.Release ?? "—");

        // ---------- Unidades e desempenho teórico ----------
        uint? cores = h != IntPtr.Zero ? Nvml.Cores(h) : null;
        int shaders = (int)(cores ?? (uint)(spec?.Shaders ?? 0));
        var units = Section(gpu, "UNIDADES DE PROCESSAMENTO");
        if (shaders > 0)
        {
            bool nv = ven == "10DE";
            int perUnit = nv ? 128 : 64;
            units.Add(nv ? "CUDA cores" : "Stream processors", $"{shaders:N0}");
            units.Add(nv ? "SMs" : "Compute units", $"{shaders / perUnit}");
            if (nv && spec != null && spec.Arch is "Blackwell" or "Ada Lovelace" or "Ampere")
            {
                units.Add("Tensor cores", $"{shaders / perUnit * 4}");
                units.Add("RT cores", $"{shaders / perUnit}");
            }
            else if (!nv && spec != null && spec.Arch.StartsWith("RDNA"))
                units.Add("Ray accelerators", $"{shaders / perUnit}");
        }
        if (spec != null)
        {
            units.Add("ROPs / TMUs", $"{spec.Rops} / {spec.Tmus}");
            units.Add("Pixel fillrate", $"{spec.Rops * spec.BoostMHz / 1000.0:0.0} GPixel/s");
            units.Add("Texture fillrate", $"{spec.Tmus * spec.BoostMHz / 1000.0:0.0} GTexel/s");
            units.Add("FP32 (teórico)", $"{(shaders > 0 ? shaders : spec.Shaders) * 2.0 * spec.BoostMHz / 1e6:0.00} TFLOPS");
        }
        if (units.Children.Count == 0) units.Add("Sem dados", "GPU fora do banco de especificações");

        // ---------- Memória ----------
        var mem = Section(gpu, "MEMÓRIA");
        ulong vram = 0;
        if (h != IntPtr.Zero && Nvml.MemoryInfo(h) is Nvml.Memory nm) vram = nm.Total;
        if (vram == 0 && Sys.ReadLong(dev + "mem_info_vram_total") is long av) vram = (ulong)av;
        gpu.VramBytes = vram;
        bool igpu = spec?.MemType.StartsWith("Memória do sistema") == true || (vram is > 0 and < 1UL << 30);
        if (igpu || vram < 1UL << 30) mem.Add(igpu ? "Reservada (dedicada)" : "Tamanho", vram > 0 ? Sys.Bytes(vram) : "—");
        else
        {
            // Driver reserva parte da VRAM: 12227 MiB reportados = placa de 12 GB (como mostra o GPU-Z).
            double gib = vram / (double)(1UL << 30);
            mem.Add("Tamanho", $"{Math.Ceiling(gib - 0.15):0} GB ({vram >> 20:N0} MB utilizáveis)");
        }
        if (Sys.ReadLong(dev + "mem_info_gtt_total") is long gtt) mem.Add("Compartilhada do sistema (GTT)", Sys.Bytes(gtt));
        mem.Add("Tipo", spec?.MemType ?? "—");
        uint? bus = h != IntPtr.Zero ? Nvml.BusWidth(h) : null;
        uint? memClock = h != IntPtr.Zero ? Nvml.MaxClock(h, Nvml.ClockMem) : null;
        if (bus is uint b) mem.Add("Barramento", $"{b} bits");
        if (memClock is uint mc)
        {
            // A NVML reporta metade da taxa efetiva (GDDR6/6X/7): 14001 MHz → 28 Gbps.
            double gbps = mc * 2 / 1000.0;
            mem.Add("Clock da memória", $"{mc} MHz ({gbps:0.0} Gbps efetivos)");
            if (bus is uint bw) mem.Add("Largura de banda", $"{gbps * bw / 8:0.0} GB/s");
        }
        ulong bar = h != IntPtr.Zero && Nvml.Bar1(h) is Nvml.Memory b1 ? b1.Total : LargestBar(Sys.Read(dev + "resource") ?? "");
        if (bar > 0 && !igpu) mem.Add("Resizable BAR", bar > 256UL * 1024 * 1024 ? $"Ativado (BAR {Sys.Bytes(bar)})" : "Desativado (ative Above 4G Decoding e ReBAR na BIOS)");

        // ---------- Clocks ----------
        var clk = Section(gpu, "CLOCKS");
        if (spec != null)
        {
            clk.Add("Base (referência)", $"{spec.BaseMHz} MHz");
            clk.Add("Boost (referência)", $"{spec.BoostMHz} MHz");
        }
        if (h != IntPtr.Zero)
        {
            if (Nvml.MaxClock(h, Nvml.ClockGraphics) is uint g) clk.Add("Máximo (curva V/F)", $"{g} MHz");
            if (Nvml.MaxClock(h, Nvml.ClockVideo) is uint vv) clk.Add("Video engine máx.", $"{vv} MHz");
        }
        else if (Sys.Read(dev + "pp_dpm_sclk") is string sclk && Regex.Matches(sclk, @"(\d+)\s*Mhz", RegexOptions.IgnoreCase) is { Count: > 0 } ms)
            clk.Add("Máximo (tabela DPM)", $"{ms[^1].Groups[1].Value} MHz");
        if (clk.Children.Count == 0) clk.Add("Sem dados", "—");

        // ---------- Barramento ----------
        var pcie = Section(gpu, "BARRAMENTO");
        uint? maxGen = (h != IntPtr.Zero ? Nvml.MaxPcieGen(h) : null) ?? GpuInfo.PcieGen(Sys.Read(dev + "max_link_speed"));
        uint? maxW = (h != IntPtr.Zero ? Nvml.MaxPcieWidth(h) : null) ?? (uint.TryParse(Sys.Read(dev + "max_link_width"), out uint mw0) ? mw0 : null);
        uint? curGen = (h != IntPtr.Zero ? Nvml.CurPcieGen(h) : null) ?? GpuInfo.PcieGen(Sys.Read(dev + "current_link_speed"));
        uint? curW = (h != IntPtr.Zero ? Nvml.CurPcieWidth(h) : null) ?? (uint.TryParse(Sys.Read(dev + "current_link_width"), out uint cw0) ? cw0 : null);
        if (maxGen is uint mg && maxW is uint mw) pcie.Add("Interface", $"{GpuInfo.PcieName(mg)} x{mw}");
        if (curGen is uint cg && curW is uint cw)
            pcie.Add("Em uso agora", $"{GpuInfo.PcieName(cg)} x{cw}" + (maxGen > curGen ? " (economia de energia em repouso)" : "")
                                      + (maxW > curW ? " · LINHAS A MENOS: confira o slot e a placa" : ""));
        pcie.Add("Endereço PCI", pci.Slot);

        // ---------- Energia ----------
        var hwmon = Directory.Exists(dev + "hwmon") ? Directory.GetDirectories(dev + "hwmon").FirstOrDefault() : null;
        if (h != IntPtr.Zero || spec?.TdpW > 0 || hwmon != null)
        {
            var pwr = Section(gpu, "ENERGIA");
            if (spec?.TdpW > 0) pwr.Add("TDP (referência)", $"{spec.TdpW} W");
            if (h != IntPtr.Zero)
            {
                if (Nvml.PowerLimitMw(h) is uint lim) pwr.Add("Limite atual", $"{lim / 1000.0:0} W");
                if (Nvml.PowerDefaultMw(h) is uint def) pwr.Add("Limite padrão da placa", $"{def / 1000.0:0} W");
                if (Nvml.PowerConstraintsMw(h) is { } pc) pwr.Add("Faixa ajustável", $"{pc.Min / 1000.0:0} – {pc.Max / 1000.0:0} W");
            }
            else if (hwmon != null)
            {
                if (Sys.ReadLong(Path.Combine(hwmon, "power1_cap")) is long cap) pwr.Add("Limite atual", $"{cap / 1e6:0} W");
                if (Sys.ReadLong(Path.Combine(hwmon, "power1_cap_default")) is long def) pwr.Add("Limite padrão da placa", $"{def / 1e6:0} W");
                if (Sys.ReadLong(Path.Combine(hwmon, "power1_cap_min")) is long mn && Sys.ReadLong(Path.Combine(hwmon, "power1_cap_max")) is long mx)
                    pwr.Add("Faixa ajustável", $"{mn / 1e6:0} – {mx / 1e6:0} W");
            }
        }

        // ---------- Driver e BIOS ----------
        var drv = Section(gpu, "DRIVER E BIOS");
        drv.Add("BIOS (VBIOS)", (h != IntPtr.Zero ? Nvml.Vbios(h) : Sys.Read(dev + "vbios_version")) ?? "—");
        drv.Add("Driver do kernel", driver switch
        {
            "nvidia" => "nvidia (proprietário)", "nouveau" => "nouveau (aberto, desempenho limitado em jogos)",
            null => "nenhum (a placa está sem driver)", var x => x,
        });
        string? ver = h != IntPtr.Zero ? Nvml.DriverVersion() : null;
        ver ??= driver != null ? Sys.Read($"/sys/module/{driver}/version") : null;
        drv.Add("Versão do driver", ver ?? $"embutido no kernel {Sys.Read("/proc/sys/kernel/osrelease")}");
        if (h != IntPtr.Zero)
        {
            if (Nvml.CudaVersion() is string cuda) drv.Add("Versão CUDA", cuda);
            if (Nvml.ComputeCapability(h) is string cc) drv.Add("Compute capability", cc);
        }

        // ---------- Tecnologias ----------
        var tech = Section(gpu, "TECNOLOGIAS");
        var apis = new List<string>();
        if (HasLib("libvulkan.so.1")) apis.Add("Vulkan");
        apis.Add("OpenGL");
        if (ven == "10DE" && HasLib("libcuda.so.1")) apis.Add("CUDA");
        if (HasLib("libOpenCL.so.1")) apis.Add("OpenCL");
        if (ven == "10DE" && HasLib("libnvidia-ngx.so.1")) apis.Add("DLSS (NGX, via Proton)");
        tech.Add("APIs", string.Join(" · ", apis));
        if (spec != null && GpuSpecDb.ArchFeatures.TryGetValue(spec.Arch, out var feats)) tech.Add("Recursos da arquitetura", feats);

        string memLabel = spec?.MemType.StartsWith("GDDR") == true ? spec.MemType : "";
        gpu.Subtitle = string.Join("  ·  ", new[]
        {
            spec?.Chip, spec?.Arch, spec?.Process,
            vram >= 1UL << 30 && !igpu ? $"{Math.Ceiling(vram / (double)(1UL << 30) - 0.15):0} GB {memLabel}".Trim() : null,
            subVen.Length > 0 ? GpuSpecDb.SubvendorName(subVen) : null
        }.Where(s => !string.IsNullOrEmpty(s)));
        return gpu;
    }

    private static readonly string[] LibDirs = { "/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu" };
    private static bool HasLib(string so) => LibDirs.Any(d => File.Exists(Path.Combine(d, so)));

    private static InfoNode Section(GpuInfo g, string title)
    {
        var n = new InfoNode { Header = title };
        g.Sections.Add(n);
        return n;
    }
}
