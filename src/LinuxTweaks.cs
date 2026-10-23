using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>
/// Ajuste do sistema para jogos: lê o estado atual, aplica e guarda o valor original (em AppSettings.TweakBackup) na primeira
/// vez que é alterado, para "Restaurar originais" desfazer tudo. Só ajustes documentados e reversíveis; nada que reduza a
/// segurança (mitigações de Spectre, firewall, atualizações).
/// </summary>
public sealed class Tweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Effect { get; init; } = "";
    public bool NeedsRoot { get; init; }
    /// <summary>Motivo para não oferecer o ajuste nesta máquina (null = disponível).</summary>
    public string? Unavailable { get; init; }
    public bool Available => Unavailable == null;
    internal Func<bool?> Read { get; init; } = () => null;
    /// <summary>Aplica (true) ou desfaz (false). Pode pedir a senha de root. Devolve null se deu certo, ou o erro.</summary>
    internal Func<bool, AppSettings, Task<string?>> Write { get; init; } = (_, _) => Task.FromResult<string?>(null);

    private bool _isOn;
    public bool IsOn { get => _isOn; set { _isOn = value; Changed(nameof(IsOn)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public string Badge => Unavailable != null ? "INDISPONÍVEL" : NeedsRoot ? "PEDE SENHA" : "";

    public void Refresh() => IsOn = Read() ?? false;

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class LinuxTweaks
{
    public const string SysctlFile = "/etc/sysctl.d/99-pharos-gaming.conf";

    public static List<Tweak> All()
    {
        bool zram = (Sys.Read("/proc/swaps") ?? "").Contains("/dev/zram");
        bool gnome = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase);
        return new()
        {
            new Tweak
            {
                Id = "power", Title = "Perfil de energia: Desempenho",
                Description = "Mantém a CPU pronta para subir o clock na hora, sem economia agressiva de energia (power-profiles-daemon).",
                Effect = "Menos engasgos e 1% low melhor em alguns jogos. Em notebooks gasta mais bateria.",
                Unavailable = Sys.Has("powerprofilesctl") ? null : "Sem power-profiles-daemon: use o GameMode na Biblioteca, que faz o mesmo só durante o jogo.",
                Read = () => BatteryInfo.PowerProfile() == "performance",
                Write = async (on, s) =>
                {
                    if (!s.TweakBackup.ContainsKey("power")) s.TweakBackup["power"] = BatteryInfo.PowerProfile();
                    string target = on ? "performance" : s.TweakBackup["power"] is string prev && prev != "performance" ? prev : "balanced";
                    var r = await Sys.RunAsync("powerprofilesctl", "set", target);
                    return r.Ok ? null : r.Err.Trim();
                },
            },
            SysctlTweak("maxmap", "vm.max_map_count", "2147483642", v => long.TryParse(v, out long x) && x >= 2147483642,
                "Limite de mapeamentos de memória no máximo (padrão do SteamOS)",
                "Alguns jogos (CS2, Hogwarts Legacy, Star Citizen, jogos da Bethesda pelo Proton) travam ou fecham com o limite baixo.",
                "Sem custo: só deixa o processo mapear mais regiões de memória quando precisa."),
            SysctlTweak("splitlock", "kernel.split_lock_mitigate", "0", v => v == "0",
                "Desligar a punição de split lock",
                "O kernel desacelera de propósito programas que fazem acesso desalinhado travando o barramento. Alguns jogos fazem isso.",
                "Recupera FPS em jogos afetados (recomendação da Valve). Não é uma mitigação de segurança.",
                Sys.Read("/proc/sys/kernel/split_lock_mitigate") == null ? "Este kernel ou processador não tem detecção de split lock." : null),
            SysctlTweak("swappiness", "vm.swappiness", "10", v => int.TryParse(v, out int x) && x <= 10,
                "Usar menos o swap (swappiness 10)",
                "Faz o kernel preferir liberar cache de disco a mandar memória do jogo para o swap em disco.",
                "Menos travadas quando a RAM está quase cheia e o swap fica no disco.",
                zram ? "Este sistema usa zram (swap comprimido na RAM): o valor alto atual é o certo e baixar pioraria." : null),
            new Tweak
            {
                Id = "mouse", Title = "Desligar aceleração do mouse",
                Description = "Perfil de aceleração \"flat\": o cursor anda sempre a mesma distância para o mesmo movimento.",
                Effect = "Mira mais consistente em jogos que usam o cursor do sistema.",
                Unavailable = gnome ? null : "Disponível no GNOME. No Hyprland use input:accel_profile = flat na configuração; no KDE, Configurações › Mouse.",
                Read = () => gnome ? Sys.RunAsync("gsettings", "get", "org.gnome.desktop.peripherals.mouse", "accel-profile").GetAwaiter().GetResult().Out.Contains("flat") : false,
                Write = async (on, s) =>
                {
                    if (!s.TweakBackup.ContainsKey("mouse"))
                        s.TweakBackup["mouse"] = (await Sys.RunAsync("gsettings", "get", "org.gnome.desktop.peripherals.mouse", "accel-profile")).Out.Trim().Trim('\'');
                    string target = on ? "flat" : s.TweakBackup["mouse"] is string prev && prev != "flat" ? prev : "default";
                    var r = await Sys.RunAsync("gsettings", "set", "org.gnome.desktop.peripherals.mouse", "accel-profile", target);
                    return r.Ok ? null : r.Err.Trim();
                },
            },
        };
    }

    private static string? ProcValue(string key) => Sys.Read("/proc/sys/" + key.Replace('.', '/'));

    /// <summary>Ajuste de sysctl persistente: uma linha no arquivo do PHAROS em /etc/sysctl.d e aplicado na hora.</summary>
    private static Tweak SysctlTweak(string id, string key, string value, Func<string, bool> isOn, string title, string desc, string effect, string? unavailable = null) => new()
    {
        Id = id, Title = title, Description = desc, Effect = effect, NeedsRoot = true, Unavailable = unavailable,
        Read = () => ProcValue(key) is string v && isOn(v),
        Write = async (on, s) =>
        {
            if (!s.TweakBackup.ContainsKey(id)) s.TweakBackup[id] = ProcValue(key);
            var lines = SetLine(Sys.Read(SysctlFile) ?? "", key, on ? value : null);
            string apply = on ? value : s.TweakBackup[id] ?? value;
            var r = await Sys.RunRootAsync(Script(lines, key, apply));
            return r.Ok ? null : Sys.RootError(r);
        },
    };

    /// <summary>Conteúdo novo do arquivo do PHAROS com a chave definida (ou removida, com value null).</summary>
    public static string SetLine(string file, string key, string? value)
    {
        var lines = file.Split('\n').Where(l => l.Trim().Length > 0 && !Regex.IsMatch(l, $@"^\s*{Regex.Escape(key)}\s*=")).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith('#')) lines.Insert(0, "# Gerado pelo PHAROS GAMING (aba Otimização). Apague para desfazer os ajustes.");
        if (value != null) lines.Add($"{key}={value}");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Script de root: grava o arquivo (ou apaga, se só sobrou o comentário) e aplica o valor na hora.</summary>
    public static string Script(string content, string key, string valueNow)
    {
        bool empty = content.Split('\n').All(l => l.Trim().Length == 0 || l.StartsWith('#'));
        string write = empty ? $"rm -f {Sys.Quote(SysctlFile)}\n"
                             : $"cat > {Sys.Quote(SysctlFile)} <<'PHAROS_EOF'\n{content}PHAROS_EOF\n";
        return write + $"sysctl -w {Sys.Quote($"{key}={valueNow}")} >/dev/null\n";
    }

    /// <summary>Desfaz tudo que o PHAROS alterou (uma senha só para os ajustes de sysctl).</summary>
    public static async Task<string?> RestoreAll(List<Tweak> tweaks, AppSettings s)
    {
        var errors = new List<string>();
        foreach (var t in tweaks.Where(t => !t.NeedsRoot && s.TweakBackup.ContainsKey(t.Id)))
            if (await t.Write(false, s) is string e) errors.Add($"{t.Title}: {e}");
        var sysctl = new Dictionary<string, string> { ["maxmap"] = "vm.max_map_count", ["splitlock"] = "kernel.split_lock_mitigate", ["swappiness"] = "vm.swappiness" };
        var restore = sysctl.Where(kv => s.TweakBackup.GetValueOrDefault(kv.Key) != null).ToList();
        if (restore.Count > 0 || File.Exists(SysctlFile))
        {
            string script = $"rm -f {Sys.Quote(SysctlFile)}\n" + string.Concat(restore.Select(kv => $"sysctl -w {Sys.Quote($"{kv.Value}={s.TweakBackup[kv.Key]}")} >/dev/null\n"));
            var r = await Sys.RunRootAsync(script);
            if (!r.Ok) errors.Add(Sys.RootError(r));
        }
        if (errors.Count == 0) s.TweakBackup.Clear();
        return errors.Count == 0 ? null : string.Join(" · ", errors);
    }
}
