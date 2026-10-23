namespace PharosGaming;

/// <summary>Família de chip USB-serial: como reconhecer, o módulo do kernel que atende e a página oficial.</summary>
public sealed record SerialChip(string Name, string[] Vids, string Module, string OfficialUrl);

/// <summary>Placa ou conversor serial conectado (/dev/ttyUSB* ou /dev/ttyACM*), com o diagnóstico.</summary>
public sealed class SerialPortInfo
{
    public string Dev { get; init; } = "";          // /dev/ttyUSB0
    public string Name { get; init; } = "";
    public string Maker { get; init; } = "";
    public string Vid { get; init; } = "";
    public string Pid { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Driver { get; init; } = "";       // driver do kernel ligado ao dispositivo
    public string? ById { get; init; }              // /dev/serial/by-id/... (nome fixo)
    public string Group { get; init; } = "";        // grupo dono do /dev/tty
    public SerialChip? Chip { get; init; }
    public List<(int Pid, string Name)> Holders { get; init; } = new();
    public bool UserHasAccess { get; init; }

    public bool Ok => UserHasAccess && Holders.Count == 0;
    public string State => Ok ? "PRONTA" : "COM PROBLEMA";
    public string Detail => string.Join(" · ", new[] { Dev, Chip?.Name ?? Maker, $"VID {Vid} PID {Pid}" }.Where(s => !string.IsNullOrEmpty(s)));
    public string DriverText => Driver.Length > 0 ? Driver : "sem driver";
    public string Diagnosis
    {
        get
        {
            var p = new List<string>();
            if (!UserHasAccess) p.Add($"Seu usuário não está no grupo \"{Group}\": a IDE vai dar \"Permission denied\" ao enviar. Use “Liberar acesso às portas seriais” (vale depois de sair e entrar de novo na sessão).");
            if (Holders.Count > 0) p.Add($"A porta está aberta por: {string.Join(", ", Holders.Select(h => $"{h.Name} ({h.Pid})"))}. Feche o monitor serial ou o programa antes de enviar.");
            if (p.Count == 0) p.Add("Tudo certo para enviar código.");
            if (ById != null) p.Add($"Nome fixo desta placa (não muda ao replugar): {ById}");
            return string.Join(" ", p);
        }
    }
}

/// <summary>
/// PHAROS MAKER no Linux: as placas aparecem como /dev/ttyUSB* (CH340, CP210x, FTDI, Prolific) ou /dev/ttyACM* (Arduino com
/// USB nativo, ESP32-S3). Os drivers já vêm no kernel; os problemas de verdade são permissão (grupo uucp/dialout), programa
/// segurando a porta, o ModemManager sondando a placa e o brltty capturando o CH340.
/// </summary>
public static class SerialPorts
{
    public static readonly SerialChip[] Chips =
    {
        new("CH340 / CH341", new[] { "1a86" }, "ch341", "https://www.wch-ic.com/downloads/CH341SER_LINUX_ZIP.html"),
        new("CP2102 / CP210x", new[] { "10c4" }, "cp210x", "https://www.silabs.com/developers/usb-to-uart-bridge-vcp-drivers"),
        new("FTDI", new[] { "0403" }, "ftdi_sio", "https://ftdichip.com/drivers/vcp-drivers/"),
        new("Prolific PL2303", new[] { "067b" }, "pl2303", "https://www.prolific.com.tw/US/ShowProduct.aspx?p_id=225&pcid=41"),
        new("Arduino / USB nativo (CDC)", new[] { "2341", "2a03", "239a", "303a", "1b4f", "2e8a" }, "cdc_acm", "https://docs.arduino.cc/software/ide-v2/"),
    };

    public static SerialChip? ChipOf(string vid) => Chips.FirstOrDefault(c => c.Vids.Contains(vid, StringComparer.OrdinalIgnoreCase));

    /// <summary>Grupo que costuma ser dono das portas seriais: uucp no Arch, dialout no Debian/Ubuntu/Fedora.</summary>
    public static string DefaultGroup() => (Sys.Read("/etc/group") ?? "").Split('\n').Any(l => l.StartsWith("uucp:")) && Sys.Has("pacman") ? "uucp" : "dialout";

    public static HashSet<string> UserGroups() =>
        Sys.RunAsync("id", "-nG").GetAwaiter().GetResult().Out.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    public static List<SerialPortInfo> List() => List("/sys/class/tty", "/dev", UserGroups(), Holders());

    /// <summary>Portas a partir do sysfs (raízes trocáveis para os testes).</summary>
    public static List<SerialPortInfo> List(string ttyRoot, string devRoot, HashSet<string> userGroups, Dictionary<string, List<(int, string)>> holders)
    {
        var list = new List<SerialPortInfo>();
        if (!Directory.Exists(ttyRoot)) return list;
        var byId = Directory.Exists(Path.Combine(devRoot, "serial/by-id"))
            ? Directory.GetFiles(Path.Combine(devRoot, "serial/by-id")).ToDictionary(f => Path.GetFileName(new FileInfo(f).ResolveLinkTarget(true)?.FullName ?? f), f => f)
            : new Dictionary<string, string>();
        foreach (var tty in Directory.GetDirectories(ttyRoot).Concat(Directory.GetFiles(ttyRoot)).Select(Path.GetFileName).OfType<string>()
                                     .Where(n => n.StartsWith("ttyUSB") || n.StartsWith("ttyACM")).OrderBy(n => n))
        {
            string dev = Path.Combine(ttyRoot, tty, "device");
            string? usb = UsbParent(dev);
            string? R(string f) => usb != null ? Sys.Read(Path.Combine(usb, f)) : null;
            string vid = R("idVendor") ?? "", group = DeviceGroup(Path.Combine(devRoot, tty));
            var driverLink = new DirectoryInfo(Path.Combine(dev, "driver"));
            list.Add(new SerialPortInfo
            {
                Dev = "/dev/" + tty, Name = R("product") ?? tty, Maker = R("manufacturer") ?? "", Vid = vid, Pid = R("idProduct") ?? "",
                Serial = R("serial") ?? "", Driver = driverLink.Exists ? driverLink.ResolveLinkTarget(false)?.Name ?? "" : "",
                ById = byId.GetValueOrDefault(tty), Group = group, Chip = ChipOf(vid),
                Holders = holders.GetValueOrDefault("/dev/" + tty) ?? new(),
                UserHasAccess = group.Length == 0 || userGroups.Contains(group),
            });
        }
        return list;
    }

    /// <summary>Sobe pela árvore do sysfs até o dispositivo USB (o diretório que tem idVendor).</summary>
    private static string? UsbParent(string device)
    {
        var d = new DirectoryInfo(device);
        string? path = d.Exists ? (d.ResolveLinkTarget(true)?.FullName ?? d.FullName) : null;
        for (int i = 0; path != null && i < 6; i++, path = Path.GetDirectoryName(path))
            if (File.Exists(Path.Combine(path, "idVendor"))) return path;
        return null;
    }

    private static string DeviceGroup(string devFile)
    {
        if (!File.Exists(devFile)) return DefaultGroup();
        var r = Sys.RunAsync("stat", "-c", "%G", devFile).GetAwaiter().GetResult();
        return r.Ok ? r.Out.Trim() : DefaultGroup();
    }

    /// <summary>Quem está com cada /dev/tty* aberto (só os processos do próprio usuário são legíveis, que são os que importam).</summary>
    public static Dictionary<string, List<(int, string)>> Holders()
    {
        var map = new Dictionary<string, List<(int, string)>>();
        foreach (var pd in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(pd), out int pid)) continue;
            try
            {
                foreach (var fd in Directory.GetFiles(Path.Combine(pd, "fd")))
                {
                    string? target = new FileInfo(fd).LinkTarget;
                    if (target == null || !(target.StartsWith("/dev/ttyUSB") || target.StartsWith("/dev/ttyACM"))) continue;
                    if (!map.TryGetValue(target, out var l)) map[target] = l = new();
                    if (!l.Any(x => x.Item1 == pid)) l.Add((pid, Sys.Read(Path.Combine(pd, "comm")) ?? pid.ToString()));
                }
            }
            catch { }
        }
        return map;
    }

    public static bool ModemManagerActive() => Sys.RunAsync("systemctl", "is-active", "ModemManager").GetAwaiter().GetResult().Out.Trim() == "active";
    public static bool BrlttyPresent() => Sys.Has("brltty") || File.Exists("/usr/lib/udev/rules.d/85-brltty.rules") || File.Exists("/lib/udev/rules.d/85-brltty.rules");

    public static bool ModuleAvailable(string module)
    {
        string root = $"/lib/modules/{Sys.Read("/proc/sys/kernel/osrelease")}/kernel/drivers/usb";
        return Directory.Exists(root) && Directory.EnumerateFiles(root, module.Replace('_', '?') + ".ko*", SearchOption.AllDirectories).Any()
               || Directory.Exists($"/sys/module/{module}");
    }

    public static bool ModuleLoaded(string module) => Directory.Exists($"/sys/module/{module}");

    // ---------------- correções (root) ----------------

    public static string AddToGroupScript(string group) =>
        $"usermod -aG {Sys.Quote(group)} {Sys.Quote(Environment.UserName)}\n";

    public const string DisableModemManagerScript = "systemctl disable --now ModemManager.service\n";
    // o brltty "pega" os conversores CH340 achando que são linhas braille; mascarar a regra udev resolve sem desinstalar
    public const string DisableBrlttyScript = "systemctl mask brltty-udev.service 2>/dev/null; systemctl stop brltty-udev.service 2>/dev/null; " +
                                              "ln -sf /dev/null /etc/udev/rules.d/85-brltty.rules; udevadm control --reload-rules\n";

    public static string LoadModuleScript(string module) => $"modprobe {Sys.Quote(module)}\n";

    /// <summary>Caches do Arduino IDE/CLI (compilação e downloads parciais). Configurações e bibliotecas ficam.</summary>
    public static IEnumerable<string> ArduinoCaches() => new[]
    {
        Path.Combine(Sys.Home, ".cache/arduino"), Path.Combine(Sys.Home, ".arduino15/staging"), Path.Combine(Sys.Home, ".cache/arduino-ide"),
    }.Where(Directory.Exists);
}
