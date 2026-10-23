using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PharosGaming;

/// <summary>Uma linha do inventário: dispositivo PCI e o driver do kernel que o atende.</summary>
public sealed class DriverRow
{
    public string Device { get; init; } = "";
    public string Class { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Driver { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Missing => Driver.Length == 0;
    public string DriverText => Missing ? "SEM DRIVER" : Driver + (Version.Length > 0 ? $" {Version}" : "");
}

/// <summary>Resultado da consulta do driver de vídeo.</summary>
public sealed class GpuDriverStatus
{
    public string Vendor { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Repo { get; set; } = "";        // versão disponível no repositório da distro
    public string Latest { get; set; } = "";      // versão mais nova publicada pela NVIDIA
    public string OfficialPage { get; set; } = "";
    public string Message { get; set; } = "";

    public static int CompareVersions(string a, string b)
    {
        static int[] P(string s) => Regex.Matches(s, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
        int[] x = P(a), y = P(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>Placa-mãe / fabricante e a página oficial de suporte (BIOS, chipset).</summary>
public sealed class BoardInfo
{
    public string Maker { get; set; } = "";
    public string Model { get; set; } = "";
    public string Bios { get; set; } = "";
    public string Brand { get; set; } = "";
    public string SupportUrl { get; set; } = "";
}

/// <summary>Atualização pendente (pacote do sistema ou firmware do LVFS).</summary>
public sealed record PendingUpdate(string Name, string From, string To, bool DriverRelated)
{
    public string Text => From.Length > 0 ? $"{From} → {To}" : To;
}

/// <summary>
/// Drivers no Linux: o kernel já traz quase todos, então o inventário mostra cada dispositivo PCI com o driver em uso (e os
/// que ficaram sem). Vídeo: versão instalada × repositório da distro × mais nova da NVIDIA (o driver se atualiza pelo
/// gerenciador de pacotes, nunca pelo instalador .run). Atualizações do sistema e de firmware (fwupd/LVFS).
/// </summary>
public static class DriverManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // ---------------- inventário ----------------

    public static List<DriverRow> Inventory()
    {
        var lines = Sys.RunAsync("lspci", "-mm", "-nn", "-D").GetAwaiter().GetResult().Out.Split('\n');
        var rows = new List<DriverRow>();
        foreach (var line in lines)
        {
            var q = Regex.Matches(line, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            int sp = line.IndexOf(' ');
            if (q.Count < 3 || sp <= 0) continue;
            string slot = line[..sp];
            string driver = SystemInfo.PciDriver(slot) ?? "";
            string Strip(string s) => Regex.Replace(s, @"\s*\[[0-9a-f]{4}\]$", "");
            rows.Add(new DriverRow
            {
                Device = Strip(q[2]), Class = Strip(q[0]), Vendor = Strip(q[1]), Driver = driver,
                Version = driver.Length > 0 ? Sys.Read($"/sys/module/{driver}/version") ?? "" : "",
            });
        }
        // bridges e controladores internos sem driver são normais; destaque só o que costuma precisar de driver
        return rows.Where(r => !r.Missing || IsUserFacing(r.Class)).OrderByDescending(r => r.Missing).ThenBy(r => r.Class).ToList();
    }

    public static bool IsUserFacing(string cls) =>
        Regex.IsMatch(cls, "VGA|3D|Display|Network|Ethernet|Wireless|Audio|USB|SATA|NVM|Bluetooth", RegexOptions.IgnoreCase);

    // ---------------- vídeo ----------------

    public static List<GpuDriverStatus> ReadGpus()
    {
        var list = new List<GpuDriverStatus>();
        foreach (var g in SystemInfo.ParseLspci(Sys.RunAsync("lspci", "-mm", "-nn", "-D").GetAwaiter().GetResult().Out))
        {
            string driver = SystemInfo.PciDriver(g.Slot) ?? "";
            var s = new GpuDriverStatus { GpuName = SystemInfo.MarketingName(g) };
            switch (g.VendorId)
            {
                case "10de":
                    s.Vendor = "NVIDIA";
                    s.OfficialPage = "https://www.nvidia.com/Download/index.aspx";
                    s.Installed = Nvml.DriverVersion() ?? Sys.Read("/sys/module/nvidia/version") ?? "";
                    if (driver == "nouveau")
                        s.Message = "Esta placa está com o nouveau (driver aberto da comunidade): em jogos rende muito menos. Instale o driver NVIDIA da sua distro (pacote nvidia-open ou nvidia).";
                    break;
                case "1002":
                    s.Vendor = "AMD";
                    s.Installed = MesaVersion();
                    s.Message = "Placas AMD usam o driver amdgpu do kernel e o Mesa: atualize o sistema para ter a versão mais nova.";
                    break;
                case "8086":
                    s.Vendor = "Intel";
                    s.Installed = MesaVersion();
                    s.Message = "Vídeo Intel usa o driver i915/xe do kernel e o Mesa: atualize o sistema para ter a versão mais nova.";
                    break;
                default:
                    s.Vendor = g.Vendor;
                    break;
            }
            if (driver.Length == 0) s.Message = "A placa está SEM driver carregado. " + s.Message;
            list.Add(s);
        }
        return list;
    }

    private static string MesaVersion()
    {
        if (Sys.Has("pacman") && Sys.RunAsync("pacman", "-Q", "mesa").GetAwaiter().GetResult() is { Ok: true } r)
            return "Mesa " + r.Out.Split(' ').Last().Trim().Split(':').Last();
        var gl = Sys.RunAsync("glxinfo", "-B").GetAwaiter().GetResult().Out;
        return Regex.Match(gl, @"Mesa [\d.]+") is { Success: true } m ? m.Value : "";
    }

    /// <summary>Versão do driver NVIDIA no repositório da distro (pacman: nvidia-utils).</summary>
    public static string? RepoNvidiaVersion()
    {
        if (!Sys.Has("pacman")) return null;
        var r = Sys.RunAsync("pacman", "-Si", "nvidia-utils").GetAwaiter().GetResult();
        return Regex.Match(r.Out, @"^Version\s*:\s*(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Split('-')[0] : null;
    }

    /// <summary>Consulta a versão mais recente para Linux 64-bit no serviço oficial da NVIDIA (o mesmo da página de drivers).</summary>
    public static async Task CheckNvidiaAsync(GpuDriverStatus g, CancellationToken ct)
    {
        g.Repo = await Task.Run(RepoNvidiaVersion, ct) ?? "";
        string? err = null;
        try
        {
            var (psid, pfid) = await ResolveNvidiaIdsAsync(g.GpuName, ct);
            string url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                         $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=12&languageCode=1033&beta=0&isWHQL=0&dch=0&sort1=0&numberOfResults=1";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            g.Latest = FindVersion(doc.RootElement) ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { err = "Não foi possível consultar a NVIDIA: " + ex.Message; }
        // a mensagem do nouveau (se houver) continua na frente
        g.Message = string.Join(" ", new[] { g.Message, NvidiaAdvice(g), err }.Where(x => !string.IsNullOrEmpty(x)));
    }

    public static string NvidiaAdvice(GpuDriverStatus g)
    {
        if (g.Installed.Length == 0) return "Driver NVIDIA não carregado.";
        bool repoNewer = g.Repo.Length > 0 && GpuDriverStatus.CompareVersions(g.Repo, g.Installed) > 0;
        bool nvNewer = g.Latest.Length > 0 && GpuDriverStatus.CompareVersions(g.Latest, g.Repo.Length > 0 ? g.Repo : g.Installed) > 0;
        if (repoNewer) return $"Há um driver mais novo na sua distro: {g.Repo} (instalado: {g.Installed}). Use ATUALIZAR O SISTEMA.";
        if (nvNewer) return $"A NVIDIA já publicou o {g.Latest}; a sua distro ainda está no {(g.Repo.Length > 0 ? g.Repo : g.Installed)}. Ele chega pelas atualizações normais em alguns dias: evite o instalador .run, que quebra a cada atualização do kernel.";
        return $"Você já está com o mais recente ({g.Installed}).";
    }

    private static string? FindVersion(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals("Version") && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                if (FindVersion(p.Value) is string v) return v;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) if (FindVersion(i) is string v) return v;
        return null;
    }

    /// <summary>Descobre os códigos de série (psid) e produto (pfid) da placa na base de drivers da NVIDIA.</summary>
    private static async Task<(int psid, int pfid)> ResolveNvidiaIdsAsync(string gpuName, CancellationToken ct)
    {
        bool mobile = Regex.IsMatch(gpuName, @"Laptop|Max-Q|Mobile|Notebook", RegexOptions.IgnoreCase);
        var m = Regex.Match(gpuName, @"(RTX|GTX|GT)\s*(\d{3,4})", RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException("Modelo de placa não reconhecido: " + gpuName);
        string prefix = m.Groups[1].Value.ToUpperInvariant();
        int n = int.Parse(m.Groups[2].Value);
        string series = prefix == "RTX" ? $"RTX {n / 100} Series"
                      : n is >= 1600 and < 1700 ? "16 Series" : n is >= 1000 and < 1100 ? "10 Series"
                      : n is >= 900 and < 1000 ? "900 Series" : n is >= 700 and < 800 ? "700 Series" : $"{n / 100 * 100} Series";
        string xml = await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2&ParentID=1", ct);
        var cand = XDocument.Parse(xml).Descendants("LookupValue")
            .Select(x => (Name: (string?)x.Element("Name") ?? "", Value: (string?)x.Element("Value") ?? ""))
            .Where(s => s.Name.Contains(series, StringComparison.OrdinalIgnoreCase) && s.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) == mobile)
            .OrderBy(s => s.Name.Length).ToList();
        if (cand.Count == 0) throw new InvalidOperationException("Série da placa não encontrada na NVIDIA (" + series + ").");
        int psid = int.Parse(cand[0].Value);
        xml = await Http.GetStringAsync($"https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID={psid}", ct);
        var prods = XDocument.Parse(xml).Descendants("LookupValue")
            .Select(x => (Name: (string?)x.Element("Name") ?? "", Value: (string?)x.Element("Value") ?? "")).ToList();
        if (prods.Count == 0) throw new InvalidOperationException("Nenhum produto listado para a série.");
        string key = Regex.Replace(gpuName, @"^NVIDIA\s+", "", RegexOptions.IgnoreCase).Replace("Laptop GPU", "").Trim();
        var pick = prods.FirstOrDefault(p => string.Equals(p.Name.Trim(), key, StringComparison.OrdinalIgnoreCase));
        if (pick.Value == null) pick = prods.FirstOrDefault(p => p.Name.Contains(m.Value, StringComparison.OrdinalIgnoreCase));
        if (pick.Value == null) pick = prods[0]; // o driver é unificado por série: qualquer produto da série devolve o mesmo
        return (psid, int.Parse(pick.Value));
    }

    // ---------------- placa-mãe ----------------

    public static BoardInfo ReadBoard()
    {
        string D(string f) => Sys.Read("/sys/class/dmi/id/" + f) ?? "";
        var b = new BoardInfo { Maker = D("board_vendor"), Model = D("board_name"), Bios = $"{D("bios_version")} ({D("bios_date")})" };
        string all = b.Maker + " " + D("sys_vendor");
        string q = Uri.EscapeDataString(b.Model.Length > 0 && !b.Model.Contains("Default", StringComparison.OrdinalIgnoreCase) ? b.Model : D("product_name"));
        (b.Brand, b.SupportUrl) = all switch
        {
            _ when all.Contains("ASUS", StringComparison.OrdinalIgnoreCase) => ("ASUS", "https://www.asus.com/support/download-center/"),
            _ when all.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || all.Contains("MSI", StringComparison.OrdinalIgnoreCase) => ("MSI", "https://www.msi.com/search/" + q),
            _ when all.Contains("GIGABYTE", StringComparison.OrdinalIgnoreCase) => ("Gigabyte", "https://www.gigabyte.com/Support"),
            _ when all.Contains("ASRock", StringComparison.OrdinalIgnoreCase) => ("ASRock", "https://www.asrock.com/support/index.asp"),
            _ when all.Contains("Dell", StringComparison.OrdinalIgnoreCase) => ("Dell", "https://www.dell.com/support/home/"),
            _ when all.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) => ("Lenovo", "https://support.lenovo.com/"),
            _ when all.Contains("HP", StringComparison.OrdinalIgnoreCase) || all.Contains("Hewlett", StringComparison.OrdinalIgnoreCase) => ("HP", "https://support.hp.com/"),
            _ when all.Contains("Acer", StringComparison.OrdinalIgnoreCase) => ("Acer", "https://www.acer.com/support"),
            _ => ("", ""),
        };
        return b;
    }

    // ---------------- atualizações ----------------

    private static readonly Regex DriverPkg = new(@"^(linux(-zen|-lts|-hardened)?|linux-firmware.*|nvidia.*|lib32-nvidia.*|mesa|lib32-mesa|vulkan-.*|amd-ucode|intel-ucode|xf86-video-.*)$", RegexOptions.Compiled);

    /// <summary>Atualizações pendentes do sistema, sem root (checkupdates no Arch; apt/dnf nos outros).</summary>
    public static async Task<List<PendingUpdate>> SystemUpdates(CancellationToken ct)
    {
        Sys.Result r;
        if (Sys.Has("checkupdates")) r = await Sys.RunAsync("checkupdates", Array.Empty<string>(), ct);
        else if (Sys.Has("apt")) r = await Sys.RunAsync("apt", new[] { "list", "--upgradable" }, ct);
        else if (Sys.Has("dnf")) r = await Sys.RunAsync("dnf", new[] { "-q", "check-update" }, ct);
        else return new();
        return ParseUpdates(r.Out);
    }

    /// <summary>Entende as três saídas: "pkg 1.0 -> 1.1" (pacman), "pkg/suite 1.1 amd64 [upgradable from: 1.0]" (apt), "pkg.arch 1.1 repo" (dnf).</summary>
    public static List<PendingUpdate> ParseUpdates(string text)
    {
        var list = new List<PendingUpdate>();
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Listing")))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string name, from = "", to;
            if (p.Length >= 4 && p[2] == "->") { name = p[0]; from = p[1]; to = p[3]; }
            else if (line.Contains("[upgradable from:")) { name = p[0].Split('/')[0]; to = p[1]; from = Regex.Match(line, @"from: ([^\]]+)").Groups[1].Value; }
            else if (p.Length == 3) { name = p[0][..(p[0].LastIndexOf('.') is int d and > 0 ? d : p[0].Length)]; to = p[1]; }
            else continue;
            list.Add(new PendingUpdate(name, from, to, DriverPkg.IsMatch(name)));
        }
        return list.OrderByDescending(u => u.DriverRelated).ThenBy(u => u.Name).ToList();
    }

    /// <summary>Script de root que atualiza o sistema inteiro (no Arch, atualização parcial não é suportada).</summary>
    public static string UpgradeScript() =>
        Sys.Has("pacman") ? "pacman -Syu --noconfirm\n"
        : Sys.Has("apt-get") ? "export DEBIAN_FRONTEND=noninteractive\napt-get update -q\napt-get upgrade -y\n"
        : Sys.Has("dnf") ? "dnf upgrade -y\n" : throw new InvalidOperationException("Gerenciador de pacotes desconhecido.");

    /// <summary>Firmware com atualização no LVFS (BIOS, SSD, periféricos), pelo fwupd.</summary>
    public static async Task<List<PendingUpdate>> FirmwareUpdates(CancellationToken ct)
    {
        if (!Sys.Has("fwupdmgr")) return new();
        var r = await Sys.RunAsync("fwupdmgr", new[] { "get-updates", "--json" }, ct);
        var list = new List<PendingUpdate>();
        try
        {
            using var doc = JsonDocument.Parse(r.Out);
            foreach (var d in doc.RootElement.GetProperty("Devices").EnumerateArray())
            {
                string name = d.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                string cur = d.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                string to = d.TryGetProperty("Releases", out var rel) && rel.GetArrayLength() > 0 && rel[0].TryGetProperty("Version", out var rv) ? rv.GetString() ?? "" : "";
                list.Add(new PendingUpdate(name, cur, to, true));
            }
        }
        catch { }
        return list;
    }
}
