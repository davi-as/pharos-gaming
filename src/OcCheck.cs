using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>Erros de hardware do log do kernel: MCE/AER/EDAC (o equivalente ao WHEA do Windows) e travamentos da GPU.</summary>
public sealed record HardwareErrors(int Whea, int GpuResets, string? LastDetail, string? Error)
{
    public int Total => Whea + GpuResets;
}

/// <summary>Apoio da aba PHAROS OC: erros de hardware no journal do kernel e ajustes da GPU como root (nvidia-smi ou sysfs).</summary>
public static class OcCheck
{
    // Linhas que indicam erro de verdade (não as de inicialização como "MCE: In-kernel MCE decoding enabled").
    private static readonly Regex Machine = new(@"\[Hardware Error\]|Machine check events logged|mce: .*(error|CPU \d+: Machine Check)|AER: (Corrected|Uncorrected|Multiple)|PCIe Bus Error|EDAC .*\b(CE|UE)\b",
                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Gpu = new(@"NVRM: Xid|amdgpu.*(GPU reset|ring \S+ timeout|GPU recovery)|i915.*GPU HANG|GPU hang",
                                            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Conta os erros nas linhas do journal (formato short-iso); a mais recente vira o detalhe.</summary>
    public static HardwareErrors Classify(IEnumerable<string> lines)
    {
        int machine = 0, gpu = 0;
        string? last = null;
        foreach (var l in lines)
        {
            bool m = Machine.IsMatch(l), g = !m && Gpu.IsMatch(l);
            if (!m && !g) continue;
            if (m) machine++; else gpu++;
            int k = l.IndexOf("kernel: ", StringComparison.Ordinal);
            last = (l.Length >= 16 ? l[..16].Replace('T', ' ') + " · " : "") + (k >= 0 ? l[(k + 8)..] : l).Trim();
        }
        return new HardwareErrors(machine, gpu, last, null);
    }

    /// <summary>Conta erros de CPU/RAM/PCIe e travamentos da GPU desde o instante informado.</summary>
    public static HardwareErrors ErrorsSince(DateTime since)
    {
        var r = Sys.RunAsync("journalctl", "-k", "--no-pager", "-o", "short-iso", "--since", "@" + new DateTimeOffset(since).ToUnixTimeSeconds())
                   .GetAwaiter().GetResult();
        if (!r.Ok && r.Out.Length == 0)
            return new HardwareErrors(0, 0, null, r.Err.Contains("permission", StringComparison.OrdinalIgnoreCase)
                ? "sem permissão para o log do kernel (entre no grupo wheel, adm ou systemd-journal)" : r.Err.Trim());
        return Classify(r.Out.Split('\n'));
    }

    public static (string Maker, string Model) Motherboard() =>
        (Sys.Read("/sys/class/dmi/id/board_vendor") ?? "", Sys.Read("/sys/class/dmi/id/board_name") ?? "");

    // ---------------- Ajustes da GPU (root) ----------------

    /// <summary>Placa no nvidia-smi pelo endereço PCI no formato da NVML ("00000000:2B:00.0").</summary>
    private static string NvSelector(GpuInfo g) =>
        Nvml.Devices().FirstOrDefault(d => d.Handle == g.NvmlHandle).Pci.BusId is { Length: > 0 } bus ? bus : "0";

    /// <summary>Script de root para aplicar limite de energia e teto de clock. Volta ao padrão ao reiniciar o PC.</summary>
    public static string ApplyScript(GpuInfo g, double watts, uint? clockCap)
    {
        if (g.Vendor == "NVIDIA")
        {
            string id = Sys.Quote(NvSelector(g));
            return $"nvidia-smi -i {id} -pl {watts:0}\n" +
                   (clockCap is uint c ? $"nvidia-smi -i {id} -lgc 210,{c}\n" : $"nvidia-smi -i {id} -rgc\n");
        }
        // amdgpu: limite de energia no hwmon, em µW
        string hw = AmdHwmon(g) ?? throw new InvalidOperationException("Esta placa não expõe o limite de energia no sysfs.");
        return $"echo {(long)(watts * 1e6)} > {Sys.Quote(Path.Combine(hw, "power1_cap"))}\n";
    }

    public static string ResetScript(GpuInfo g)
    {
        if (g.Vendor == "NVIDIA")
        {
            string id = Sys.Quote(NvSelector(g));
            uint def = (Nvml.PowerDefaultMw(g.NvmlHandle) ?? 0) / 1000;
            return (def > 0 ? $"nvidia-smi -i {id} -pl {def}\n" : "") + $"nvidia-smi -i {id} -rgc\n";
        }
        string hw = AmdHwmon(g) ?? throw new InvalidOperationException("Esta placa não expõe o limite de energia no sysfs.");
        long def2 = Sys.ReadLong(Path.Combine(hw, "power1_cap_default")) ?? 0;
        return def2 > 0 ? $"echo {def2} > {Sys.Quote(Path.Combine(hw, "power1_cap"))}\n" : "true\n";
    }

    public static string? AmdHwmon(GpuInfo g)
    {
        string dir = $"/sys/bus/pci/devices/{g.Slot}/hwmon";
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "power1_cap"))) : null;
    }

    /// <summary>Faixa e valor atual do limite de energia, em W.</summary>
    public static (double Min, double Max, double Current, double Default)? PowerRange(GpuInfo g)
    {
        if (g.NvmlHandle != IntPtr.Zero)
        {
            if (Nvml.PowerConstraintsMw(g.NvmlHandle) is not { } pc || Nvml.PowerLimitMw(g.NvmlHandle) is not uint cur) return null;
            return (pc.Min / 1000.0, pc.Max / 1000.0, cur / 1000.0, (Nvml.PowerDefaultMw(g.NvmlHandle) ?? cur) / 1000.0);
        }
        if (AmdHwmon(g) is not string hw) return null;
        double W(string f) => (Sys.ReadLong(Path.Combine(hw, f)) ?? 0) / 1e6;
        double c = W("power1_cap");
        return c > 0 ? (W("power1_cap_min"), Math.Max(c, W("power1_cap_max")), c, W("power1_cap_default") is > 0 and var d ? d : c) : null;
    }
}
