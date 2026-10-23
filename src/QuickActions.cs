using System.Globalization;
using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>
/// Ações do menu rápido que não são do jogo: saída e volume de áudio (wpctl), as outras telas (DPMS do Hyprland),
/// desligar o controle (Bluetooth) e a bateria dele (power_supply do kernel).
/// </summary>
public static partial class QuickActions
{
    // ---------------- áudio ----------------

    public sealed record Sink(int Id, string Name, bool Default);

    /// <summary>Saídas do "wpctl status" (só a seção Audio › Sinks; o * marca a padrão).</summary>
    public static List<Sink> Sinks(string status)
    {
        var list = new List<Sink>();
        bool audio = false, sinks = false;
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("Audio")) audio = true;
            else if (line.StartsWith("Video") || line.StartsWith("Settings")) audio = false;
            if (!audio) continue;
            if (line.Contains("Sinks:")) { sinks = true; continue; }
            if (line.Contains("Sources:") || line.Contains("Filters:") || line.Contains("Streams:")) sinks = false;
            if (sinks && SinkRe().Match(line) is { Success: true } m)
                list.Add(new Sink(int.Parse(m.Groups[2].Value), m.Groups[3].Value.Trim(), m.Groups[1].Value == "*"));
        }
        return list;
    }

    [GeneratedRegex(@"│\s+(\*?)\s*(\d+)\.\s+(.+?)\s+\[vol:")] private static partial Regex SinkRe();

    /// <summary>Nome curto para a TV: "Fosi Audio SK02 Analog Stereo" → "Fosi Audio SK02"; o HDMI vira "TV (HDMI)".</summary>
    public static string Short(string name)
    {
        if (name.Contains("HDMI")) return "TV (HDMI)";
        if (name.Contains("IEC958") || name.Contains("S/PDIF")) return "Óptica";
        string n = Regex.Replace(name, @"\s+(Analog|Digital)\s+Stereo.*$", "");
        return Regex.Replace(n, @"\s+Audio\b", ""); // "Fosi Audio SK02" → "Fosi SK02": cabe no botão
    }

    public static async Task<List<Sink>> ReadSinks() => Sinks((await Sys.RunAsync("wpctl", "status")).Out);

    public static async Task NextSink()
    {
        var s = await ReadSinks();
        if (s.Count < 2) return;
        int i = s.FindIndex(x => x.Default);
        await Sys.RunAsync("wpctl", "set-default", s[(i + 1) % s.Count].Id.ToString());
    }

    /// <summary>"Volume: 0.50" (ou "Volume: 0.50 [MUTED]") → 50.</summary>
    public static int? ParseVolume(string o) =>
        VolRe().Match(o) is { Success: true } m ? (int)Math.Round(100 * double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : null;

    [GeneratedRegex(@"Volume:\s*([\d.]+)")] private static partial Regex VolRe();

    public static async Task<int?> Volume() => ParseVolume((await Sys.RunAsync("wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@")).Out);

    /// <summary>De 5 em 5, até 100% (o -l 1.0 do wpctl não deixa passar do volume cheio).</summary>
    public static Task Step(int delta) =>
        Sys.RunAsync("wpctl", "set-volume", "-l", "1.0", "@DEFAULT_AUDIO_SINK@", $"{Math.Abs(delta)}%{(delta > 0 ? "+" : "-")}");

    // ---------------- outras telas ----------------

    /// <summary>Alguma tela além da do jogo está desligada (DPMS)?</summary>
    public static bool OthersOff(string? keep)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Sys.RunAsync("hyprctl", "monitors", "-j").GetAwaiter().GetResult().Out);
            return doc.RootElement.EnumerateArray().Any(m => m.GetProperty("name").GetString() != keep && !m.GetProperty("dpmsStatus").GetBoolean());
        }
        catch (Exception) { return false; }
    }

    /// <summary>Desliga (DPMS) todas as telas menos a do jogo; ligar devolve todas.</summary>
    public static async Task OtherScreens(bool off, string? keep)
    {
        foreach (var m in FrameLimiter.Screens().Where(m => m.Name != keep))
            await Sys.RunAsync("hyprctl", "dispatch", $"hl.dsp.dpms({{ action = \"{(off ? "disable" : "enable")}\", monitor = \"{m.Name}\" }})");
    }

    // ---------------- controle ----------------

    /// <summary>Desconecta os controles do config.env (XBOX_MAC, PRO_MAC): sem conexão eles se desligam sozinhos.</summary>
    public static async Task<int> DisconnectPads()
    {
        if (!File.Exists(ConsoleSetup.MachineFile)) return 0;
        var env = ConsoleSetup.ParseEnv(File.ReadAllText(ConsoleSetup.MachineFile));
        int n = 0;
        foreach (var key in new[] { "XBOX_MAC", "PRO_MAC" })
            if (env.TryGetValue(key, out var mac) && MacRe().IsMatch(mac) && (await Sys.RunAsync("bluetoothctl", "disconnect", mac)).Ok) n++;
        return n;
    }

    [GeneratedRegex(@"^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")] private static partial Regex MacRe();

    public sealed record Battery(string Name, int? Percent, string? Level);

    /// <summary>Baterias de controles (scope Device): o Xbox dá a %, o 8BitDo só o nível (Normal, Low...).</summary>
    public static List<Battery> PadBatteries(string root = "/sys/class/power_supply")
    {
        var list = new List<Battery>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Sys.Read(Path.Combine(dir, "scope")) != "Device") continue;
            string id = Path.GetFileName(dir);
            string name = id.Contains("nintendo") ? "8BitDo" : id.Contains("xbox") || id.Contains("microsoft") || id.Contains("xpadneo") ? "Xbox" : "Controle";
            int? pct = int.TryParse(Sys.Read(Path.Combine(dir, "capacity")), out int p) ? p : null;
            list.Add(new Battery(name, pct, Sys.Read(Path.Combine(dir, "capacity_level"))));
        }
        return list;
    }

    public static string Text(Battery b) => b.Percent is int p ? $"{b.Name} {p}%" : b.Level switch
    {
        "Full" => $"{b.Name} cheio", "High" => $"{b.Name} alta", "Normal" => $"{b.Name} ok", "Low" => $"{b.Name} baixa",
        "Critical" => $"{b.Name} acabando", _ => b.Name,
    };

    public static bool Low(Battery b) => b.Percent is int p ? p <= 20 : b.Level is "Low" or "Critical";
}
