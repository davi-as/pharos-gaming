namespace PharosGaming;

/// <summary>Programa que abre junto com a sessão: entrada de autostart XDG (.desktop) ou serviço de usuário do systemd.</summary>
public sealed class StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Source { get; init; } = "";          // ex.: "Autostart · usuário"
    public bool Enabled { get; set; }
    public string Detail { get; init; } = "";
    public bool FileMissing { get; init; }
    /// <summary>Serviço essencial da sessão (áudio, portal...): mostrado, mas sem desligar.</summary>
    public bool Essential { get; init; }

    internal string Kind { get; init; } = "";          // Desktop | Service
    internal string Id { get; init; } = "";            // nome do .desktop ou da unidade
    internal string? UserFile { get; init; }           // .desktop em ~/.config/autostart (se existir)
    internal bool SystemFile { get; init; }            // existe também em /etc/xdg/autostart

    public bool CanToggle => !Essential && !FileMissing;
    public bool CanRemove => Kind == "Desktop" && UserFile != null && !SystemFile;
    public string State => FileMissing ? "PROGRAMA NÃO EXISTE" : Enabled ? "ATIVADO" : "DESATIVADO";
}

/// <summary>Item removido, guardado para "Restaurar removidos".</summary>
public sealed class StartupBackup
{
    public string Name { get; set; } = "";
    public string? Backup { get; set; }    // cópia do .desktop em ~/.config/pharos-gaming/Startup
    public string? Original { get; set; }  // caminho original
}

/// <summary>
/// Lista, ativa/desativa e remove programas de inicialização. Desativar um .desktop do sistema cria a cópia do usuário com
/// Hidden=true (padrão XDG: o original continua lá e pode ser religado); remover tira o .desktop do usuário, com cópia para
/// desfazer. Serviços de usuário do systemd ligam e desligam com systemctl --user.
/// </summary>
public static class StartupManager
{
    public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
    public const string SystemDir = "/etc/xdg/autostart";
    public static string BackupFolder => Path.Combine(AppSettings.Folder, "Startup");

    private static readonly HashSet<string> EssentialServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "pipewire.service", "pipewire-pulse.service", "wireplumber.service", "pulseaudio.service", "dbus.service", "dbus-broker.service",
        "xdg-desktop-portal.service", "xdg-desktop-portal-hyprland.service", "xdg-user-dirs-update.service", "gnome-keyring-daemon.service",
        "p11-kit-server.service", "gpg-agent.service", "ssh-agent.service", "at-spi-dbus-bus.service", "uwsm-app@.service", "waybar.service",
        "hypridle.service", "hyprpolkitagent.service", "pipewire.socket", "pipewire-pulse.socket", "systemd-tmpfiles-setup.service",
    };

    public static List<StartupEntry> List() =>
        ListDesktop(SystemDir, UserDir, Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Concat(ListServices()).OrderBy(e => e.Essential).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // ---------------- .desktop (autostart XDG) ----------------

    public static Dictionary<string, string> ParseDesktop(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.Trim();
            if (l.StartsWith('[')) { inEntry = l == "[Desktop Entry]"; continue; }
            int i = l.IndexOf('=');
            if (!inEntry || i <= 0 || l.StartsWith('#')) continue;
            d.TryAdd(l[..i].Trim(), l[(i + 1)..].Trim());
        }
        return d;
    }

    /// <summary>Respeita OnlyShowIn/NotShowIn: entradas que não rodam neste ambiente não aparecem.</summary>
    public static bool AppliesTo(Dictionary<string, string> d, string desktop)
    {
        var mine = desktop.Split(':', StringSplitOptions.RemoveEmptyEntries);
        static string[] L(string? v) => (v ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (d.TryGetValue("OnlyShowIn", out var only) && !L(only).Intersect(mine, StringComparer.OrdinalIgnoreCase).Any()) return false;
        if (d.TryGetValue("NotShowIn", out var not) && L(not).Intersect(mine, StringComparer.OrdinalIgnoreCase).Any()) return false;
        return true;
    }

    public static List<StartupEntry> ListDesktop(string systemDir, string userDir, string desktop)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { systemDir, userDir })
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.desktop")) ids.Add(Path.GetFileName(f));
        var list = new List<StartupEntry>();
        foreach (var id in ids)
        {
            string sys = Path.Combine(systemDir, id), usr = Path.Combine(userDir, id);
            bool hasSys = File.Exists(sys), hasUsr = File.Exists(usr);
            // a cópia do usuário vence a do sistema (mesmo nome)
            var d = ParseDesktop(Sys.Read(hasUsr ? usr : sys) ?? "");
            var full = hasSys && hasUsr ? ParseDesktop(Sys.Read(sys) ?? "") : d;
            foreach (var (k, v) in full) d.TryAdd(k, v);
            if (!AppliesTo(d, desktop)) continue;
            bool hidden = d.GetValueOrDefault("Hidden") == "true" || d.GetValueOrDefault("X-GNOME-Autostart-enabled") == "false";
            string exec = d.GetValueOrDefault("Exec", "");
            string bin = exec.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => !t.Contains('=')) ?? "";
            bool missing = bin.Length > 0 && (bin.StartsWith('/') ? !File.Exists(bin) : !Sys.Has(bin) && bin != "env" && bin != "sh");
            list.Add(new StartupEntry
            {
                Name = d.GetValueOrDefault("Name", Path.GetFileNameWithoutExtension(id)), Command = exec,
                Source = hasSys ? "Autostart · sistema" : "Autostart · usuário", Enabled = !hidden,
                Detail = d.GetValueOrDefault("Comment", ""), FileMissing = missing,
                Kind = "Desktop", Id = id, UserFile = hasUsr ? usr : null, SystemFile = hasSys,
            });
        }
        return list;
    }

    // ---------------- serviços do systemd (usuário) ----------------

    private static List<StartupEntry> ListServices()
    {
        var r = Sys.RunAsync("systemctl", "--user", "list-unit-files", "--type=service,socket", "--no-legend", "--no-pager").GetAwaiter().GetResult();
        if (!r.Ok) return new();
        var list = new List<StartupEntry>();
        foreach (var line in r.Out.Split('\n'))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[1] is not ("enabled" or "disabled") || p[0].Contains('@')) continue;
            // só o que o usuário (ou um pacote) ligou: "disabled" entra se já foi ligado pelo PHAROS antes (para poder religar)
            if (p[1] == "disabled" && !AppSettings.Current.StartupDisabledServices.Contains(p[0])) continue;
            list.Add(new StartupEntry
            {
                Name = p[0].Replace(".service", "").Replace(".socket", " (socket)"), Command = "systemctl --user " + p[0],
                Source = "Serviço do usuário (systemd)", Enabled = p[1] == "enabled", Essential = EssentialServices.Contains(p[0]),
                Detail = EssentialServices.Contains(p[0]) ? "Essencial para a sessão (áudio, portais ou barra): não dá para desligar aqui." : "",
                Kind = "Service", Id = p[0],
            });
        }
        return list;
    }

    // ---------------- ações ----------------

    public static async Task SetEnabled(StartupEntry e, bool on)
    {
        if (e.Kind == "Service")
        {
            var r = await Sys.RunAsync("systemctl", "--user", on ? "enable" : "disable", e.Id);
            if (!r.Ok) throw new InvalidOperationException(r.Err.Trim());
            var set = AppSettings.Current.StartupDisabledServices;
            if (on) set.Remove(e.Id); else if (!set.Contains(e.Id)) set.Add(e.Id);
            AppSettings.Current.Save();
            return;
        }
        Directory.CreateDirectory(UserDir);
        string usr = Path.Combine(UserDir, e.Id);
        string text = Sys.Read(usr) ?? Sys.Read(Path.Combine(SystemDir, e.Id)) ?? throw new FileNotFoundException(e.Id);
        File.WriteAllText(usr, SetHidden(text, !on));
    }

    /// <summary>Liga/desliga a chave Hidden dentro de [Desktop Entry] (e a do GNOME, se houver), preservando o resto.</summary>
    public static string SetHidden(string text, bool hidden)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("Hidden=") && !l.StartsWith("X-GNOME-Autostart-enabled=")).ToList();
        int i = lines.FindIndex(l => l.Trim() == "[Desktop Entry]");
        if (i < 0) { lines.Insert(0, "[Desktop Entry]"); i = 0; }
        lines.Insert(i + 1, $"Hidden={(hidden ? "true" : "false")}");
        return string.Join("\n", lines).TrimEnd('\n') + "\n";
    }

    public static StartupBackup Remove(StartupEntry e)
    {
        if (!e.CanRemove || e.UserFile == null) throw new InvalidOperationException("Só dá para remover entradas criadas na pasta do usuário.");
        Directory.CreateDirectory(BackupFolder);
        string backup = Path.Combine(BackupFolder, $"{DateTime.Now:yyyyMMddHHmmss}-{e.Id}");
        File.Move(e.UserFile, backup);
        return new StartupBackup { Name = e.Name, Backup = backup, Original = e.UserFile };
    }

    public static void Restore(StartupBackup b)
    {
        if (b.Backup == null || b.Original == null || !File.Exists(b.Backup)) throw new FileNotFoundException("A cópia de segurança não existe mais.");
        Directory.CreateDirectory(Path.GetDirectoryName(b.Original)!);
        File.Move(b.Backup, b.Original, overwrite: false);
    }
}
