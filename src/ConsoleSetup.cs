using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>
/// PC como console: o ecossistema do dotfiles (gaming/) visto e ajustado pelo PHAROS. O PHAROS não reimplementa nada:
/// edita os mesmos arquivos (buttons.env do daemon do controle, autostart do Hyprland, wrapper do Hydra, MangoHud.conf),
/// controla os serviços pelo systemctl --user e lê o diagnóstico do gaming-doctor.
/// </summary>
public static partial class ConsoleSetup
{
    public static string ButtonsFile => Path.Combine(Sys.ConfigHome, "console", "buttons.env");
    public static string MachineFile => Path.Combine(Sys.ConfigHome, "console", "config.env");
    public static string AutostartFile => Path.Combine(Sys.ConfigHome, "hypr", "autostart.lua");
    public static string MangoConf => Path.Combine(Sys.ConfigHome, "MangoHud", "MangoHud.conf");
    public static string Bin(string name) => Path.Combine(Sys.Home, ".local", "bin", name);
    public static string HydraWrapper => Bin("hydra");

    public const string ControllerService = "xbox-guide", BluetoothService = "xbox-bt-autoconnect";

    // ---------------- atalhos do controle (buttons.env, lido pelo daemon em ~/.local/lib/console) ----------------

    public sealed record Choice(string Id, string Label);

    /// <summary>Mesmo catálogo do buttons.py (LABELS): uma ação que o daemon não conhece volta ao padrão lá.</summary>
    public static readonly Choice[] Actions =
    {
        new("hydra", "Abrir o Hydra"),
        new("steam_bp", "Abrir o Steam Big Picture"),
        new("mangohud", "Mostrar/esconder MangoHud"),
        new("screenshot", "Capturar a tela"),
        new("tv_pc", "TV na entrada do PC"),
        new("suspend", "Suspender (e desligar a TV)"),
        new("none", "Nada"),
    };

    public sealed record Gesture(string Key, string Label, string Hint, string Default);

    public static readonly Gesture[] Gestures =
    {
        new("GUIDE_TAP", "Guide", "toque rápido no botão Xbox (Home no 8BitDo)", "hydra"),
        new("GUIDE_HOLD", "Guide segurado", "segurar o botão Xbox", "suspend"),
        new("GUIDE_START", "Guide + Start", "Home + Plus no 8BitDo", "tv_pc"),
        new("VIEW_START", "View + Start", "Minus + Plus no 8BitDo", "mangohud"),
        new("SHARE", "Share", "Capture no 8BitDo", "screenshot"),
    };

    /// <summary>KEY=valor (o mesmo formato do config.env, que o bash lê com source). Comentários e linhas vazias ficam de fora.</summary>
    public static Dictionary<string, string> ParseEnv(string text)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    public static Dictionary<string, string> LoadButtons()
    {
        var d = Gestures.ToDictionary(g => g.Key, g => g.Default);
        d["GUIDE_VOLUME"] = "1";
        d["HOLD_SECONDS"] = "2";
        try
        {
            if (File.Exists(ButtonsFile))
                foreach (var (k, v) in ParseEnv(File.ReadAllText(ButtonsFile)))
                    if (d.ContainsKey(k)) d[k] = v;
        }
        catch (IOException) { }
        return d;
    }

    public static string BuildButtons(IReadOnlyDictionary<string, string> d) =>
        "# Atalhos do controle (daemon do console, serviço xbox-guide). Gerado pelo PHAROS GAMING (PC como console).\n"
        + "# Ações: " + string.Join(", ", Actions.Select(a => a.Id)) + ". HOLD_SECONDS entre 1 e 3.\n"
        + string.Join("\n", Gestures.Select(g => $"{g.Key}={d[g.Key]}"))
        + $"\nGUIDE_VOLUME={d["GUIDE_VOLUME"]}\nHOLD_SECONDS={d["HOLD_SECONDS"]}\n";

    /// <summary>Grava e reinicia o daemon (ele lê o arquivo ao subir; o controle volta em ~40 ms).</summary>
    public static async Task<string?> SaveButtons(IReadOnlyDictionary<string, string> d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ButtonsFile)!);
        File.WriteAllText(ButtonsFile + ".tmp", BuildButtons(d));
        File.Move(ButtonsFile + ".tmp", ButtonsFile, overwrite: true);
        var r = await Sys.RunAsync("systemctl", "--user", "restart", ControllerService);
        return r.Ok ? null : Sys.RootError(r);
    }

    // ---------------- serviços ----------------

    public sealed record ServiceState(bool Exists, bool Active, bool Enabled);

    public static async Task<ServiceState> Service(string unit)
    {
        var active = await Sys.RunAsync("systemctl", "--user", "is-active", unit);
        var enabled = await Sys.RunAsync("systemctl", "--user", "is-enabled", unit);
        bool exists = enabled.Out.Trim() is { Length: > 0 } st && st != "not-found"; // systemd novo responde not-found; o antigo, só erro no stderr
        return new ServiceState(exists, active.Out.Trim() == "active", enabled.Out.Trim() == "enabled");
    }

    public static async Task<string?> SetService(string unit, bool on)
    {
        var r = await Sys.RunAsync("systemctl", "--user", on ? "enable" : "disable", "--now", unit);
        return r.Ok ? null : Sys.RootError(r);
    }

    public static async Task<string?> RestartService(string unit)
    {
        var r = await Sys.RunAsync("systemctl", "--user", "restart", unit);
        return r.Ok ? null : Sys.RootError(r);
    }

    // ---------------- linhas de arquivo: autostart do Hyprland e wrapper do Hydra ----------------

    /// <summary>Linha que casa: true se ativa, false se comentada, null se não existe.</summary>
    public static bool? LineState(string text, Func<string, bool> match, string comment)
    {
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.Trim();
            bool commented = l.StartsWith(comment);
            if (match(commented ? l[comment.Length..].Trim() : l)) return !commented;
        }
        return null;
    }

    /// <summary>Comenta ou descomenta a primeira linha que casa (preserva o resto do arquivo como está).</summary>
    public static string SetLine(string text, Func<string, bool> match, string comment, bool on)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            bool commented = l.StartsWith(comment);
            string code = commented ? l[comment.Length..].Trim() : l;
            if (!match(code)) continue;
            string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
            lines[i] = indent + (on ? code : $"{comment} {code}");
            break;
        }
        return string.Join('\n', lines);
    }

    public static bool IsHydraAutostart(string code) => code.StartsWith("o.launch_on_start(") && HydraRe().IsMatch(code);
    public static bool IsSteamAutostart(string code) => code.StartsWith("o.launch_on_start(") && SteamRe().IsMatch(code);
    public static bool IsMangoExport(string code) => code == "export MANGOHUD=1";

    [GeneratedRegex(@"/hydra\b")] private static partial Regex HydraRe();
    [GeneratedRegex(@"[""\s=]steam -silent")] private static partial Regex SteamRe();

    /// <summary>A linha da Steam no autostart com ou sem MANGOHUD=1 (sem ela, jogos da Steam abertos pelo Hydra ficam sem MangoHud).</summary>
    public static string SetSteamMango(string text, bool on) =>
        SteamLineRe().Replace(text, m => m.Groups[1].Value + (on ? "env MANGOHUD=1 " : "") + "steam -silent", 1);

    public static bool SteamMango(string text) => text.Split('\n').Any(l => IsSteamAutostart(l.Trim()) && l.Contains("MANGOHUD=1"));

    [GeneratedRegex(@"(o\.launch_on_start\("")(?:env MANGOHUD=1 )?steam -silent")] private static partial Regex SteamLineRe();

    /// <summary>Lê, transforma e grava com cópia (.pharos-bak) na primeira vez que o PHAROS mexe no arquivo.</summary>
    public static void EditFile(string path, Func<string, string> change)
    {
        string before = File.ReadAllText(path);
        string after = change(before);
        if (after == before) return;
        if (!File.Exists(path + ".pharos-bak")) File.Copy(path, path + ".pharos-bak");
        var mode = File.GetUnixFileMode(path); // o wrapper do Hydra é executável
        File.WriteAllText(path + ".tmp", after);
        File.SetUnixFileMode(path + ".tmp", mode);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // ---------------- MangoHud.conf ----------------

    /// <summary>no_display (ou no_display=1) = o overlay começa escondido.</summary>
    public static bool MangoHidden(string conf) =>
        conf.Split('\n').Select(l => l.Trim()).Any(l => l == "no_display" || l == "no_display=1");

    public static string SetMangoHidden(string conf, bool hidden)
    {
        var lines = conf.TrimEnd('\n').Split('\n').Where(l => !l.Trim().StartsWith("no_display")).ToList();
        if (hidden) lines.Add("no_display=1");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>"Control_L+Shift_L+Q" → "Ctrl + Shift + Q" (padrão do MangoHud quando não há linha: Shift_R+F12).</summary>
    public static string MangoToggleKeys(string conf)
    {
        string keys = conf.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("toggle_hud="))?[11..] ?? "Shift_R+F12";
        return string.Join(" + ", keys.Split('+').Select(k => k.Replace("Control_", "Ctrl_").Split('_')[0]));
    }

        // ---------------- processos ----------------

    /// <summary>Processo principal (sem --type=, que são os filhos do Electron/CEF) e se ele tem MANGOHUD=1 no ambiente.</summary>
    public static (bool Running, bool Mango) Process(Func<string, bool> exeMatch)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
            try
            {
                string exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget ?? "";
                if (!exeMatch(exe)) continue;
                var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0');
                if (args.Any(a => a.StartsWith("--type="))) continue;
                return (true, File.ReadAllText(Path.Combine(dir, "environ")).Split('\0').Contains("MANGOHUD=1"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (false, false);
    }

    public static (bool Running, bool Mango) Hydra() => Process(e => e.EndsWith("/hydralauncher"));
    public static (bool Running, bool Mango) Steam() => Process(e => e.EndsWith("/ubuntu12_32/steam"));

    /// <summary>Controles de jogo conectados (nomes do /proc/bus/input/devices), sem os sensores de movimento.</summary>
    public static List<string> Controllers(string devices) =>
        NameRe().Matches(devices).Select(m => m.Groups[1].Value)
            .Where(n => (n.Contains("Controller") || n.Contains("Xbox") || n.Contains("Gamepad") || n.Contains("8BitDo"))
                        && !n.Contains("IMU") && !n.Contains("Motion") && !n.Contains("Consumer") && !n.Contains("Keyboard"))
            .Distinct().ToList();

    [GeneratedRegex(@"^N: Name=""(.*)""$", RegexOptions.Multiline)] private static partial Regex NameRe();

    // ---------------- diagnóstico (gaming-doctor) ----------------

    public sealed record Check(string Section, char Mark, string Text);

    /// <summary>Saída do gaming-doctor: seção sem recuo, itens "  ✓|!|✗ texto" (cores ANSI removidas).</summary>
    public static List<Check> ParseDoctor(string output)
    {
        var list = new List<Check>();
        string section = "";
        foreach (var raw in AnsiRe().Replace(output, "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            string t = raw.Trim();
            if (!raw.StartsWith(' '))
            {
                if (t.Length > 1 && t[0] is not ('✓' or '!' or '✗')) section = t;
                continue;
            }
            if (t.Length > 2 && t[0] is ('✓' or '!' or '✗') && t[1] == ' ') list.Add(new Check(section, t[0], t[2..]));
        }
        return list;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex AnsiRe();
}
