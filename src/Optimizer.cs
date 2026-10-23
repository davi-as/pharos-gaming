using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PharosGaming;

/// <summary>Programa (agrupado pelo nome do executável) que pode ser fechado para liberar recursos.</summary>
public sealed class BackgroundApp : INotifyPropertyChanged
{
    public string ProcessName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public long MemoryBytes { get; init; }
    public int Count { get; init; }
    public bool HasWindow { get; init; }
    public bool Suggested { get; init; }
    public List<int> Pids { get; init; } = new();
    public string MemoryText => $"{MemoryBytes / 1048576.0:N0} MB";

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record CleanupResult(long FreedBytes, bool Dropped, string? Error);
public sealed record CloseResult(List<string> Closed, List<string> NotClosed);

/// <summary>Limpeza de memória e fechamento de programas em segundo plano (só os do usuário; nada do sistema).</summary>
public static class Optimizer
{
    /// <summary>Processos que nunca aparecem na lista (sessão gráfica, áudio, shell, o próprio app).</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "sd-pam", "dbus-daemon", "dbus-broker", "dbus-broker-launch", "Hyprland", "hyprland", "Xwayland", "Xorg", "gnome-shell",
        "kwin_wayland", "kwin_x11", "plasmashell", "sway", "waybar", "mako", "swaync", "dunst", "hypridle", "hyprlock", "hyprpaper",
        "hyprsunset", "swaybg", "swayosd-server", "walker", "elephant", "pipewire", "pipewire-pulse", "wireplumber", "pulseaudio",
        "xdg-desktop-portal", "xdg-desktop-portal-hyprland", "xdg-desktop-portal-gtk", "xdg-document-portal", "xdg-permission-store",
        "gnome-keyring-daemon", "polkit-gnome-authentication-agent-1", "hyprpolkitagent", "at-spi-bus-launcher", "at-spi2-registryd",
        "gvfsd", "gvfsd-fuse", "uwsm", "bash", "zsh", "fish", "sh", "tmux", "ssh-agent", "gpg-agent", "pharos-gaming", "dotnet",
        "alacritty", "kitty", "ghostty", "foot", "fcitx5", "ibus-daemon", "kanshi", "wl-paste", "cliphist", "gamemoded", "mangohud",
    };

    /// <summary>Programas de segundo plano seguros de fechar durante o jogo (pré-marcados).</summary>
    private static readonly Dictionary<string, string> SuggestedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dropbox"] = "Sincronização do Dropbox (pode usar disco e rede)",
        ["nextcloud"] = "Sincronização do Nextcloud",
        ["megasync"] = "Sincronização do MEGA",
        ["insync"] = "Sincronização do Google Drive (Insync)",
        ["onedrive"] = "Sincronização do OneDrive",
        ["teams-for-linux"] = "Microsoft Teams",
        ["slack"] = "Slack",
        ["zoom"] = "Zoom",
        ["skypeforlinux"] = "Skype",
        ["tracker-miner-fs-3"] = "Indexador de arquivos do GNOME",
        ["baloo_file"] = "Indexador de arquivos do KDE",
        ["baloo_file_extractor"] = "Indexador de arquivos do KDE",
        ["localsearch-3"] = "Indexador de arquivos do GNOME",
    };

    /// <summary>PIDs com janela, pelo compositor (Hyprland); nos outros ambientes não há como saber sem X11.</summary>
    private static HashSet<int> WindowPids()
    {
        var set = new HashSet<int>();
        if (Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") == null) return set;
        try
        {
            using var doc = JsonDocument.Parse(Sys.RunAsync("hyprctl", "clients", "-j").GetAwaiter().GetResult().Out);
            foreach (var c in doc.RootElement.EnumerateArray()) if (c.TryGetProperty("pid", out var p)) set.Add(p.GetInt32());
        }
        catch { }
        return set;
    }

    private static int? Uid(int pid) =>
        (Sys.Read($"/proc/{pid}/status") ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("Uid:")) is string l && int.TryParse(l.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1], out int u) ? u : null;

    public static List<BackgroundApp> ListApps()
    {
        int me = Uid(Environment.ProcessId) ?? -1, self = Environment.ProcessId;
        var windows = WindowPids();
        var groups = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || Uid(p.Id) != me || Protected.Contains(p.ProcessName)) continue;
                if (!groups.TryGetValue(p.ProcessName, out var list)) groups[p.ProcessName] = list = new List<Process>();
                list.Add(p);
            }
            catch { }
        }
        var result = new List<BackgroundApp>();
        foreach (var (name, procs) in groups)
        {
            bool hasWindow = procs.Any(p => windows.Contains(p.Id));
            bool suggested = SuggestedApps.ContainsKey(name);
            if (!hasWindow && !suggested) continue; // processos auxiliares sem janela ficam de fora
            string detail = suggested ? SuggestedApps[name] : "Programa aberto";
            if (procs.Count > 1) detail += $" · {procs.Count} processos";
            long mem = procs.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } });
            result.Add(new BackgroundApp
            {
                ProcessName = name, Title = Title(procs[0]) ?? name, Detail = detail, MemoryBytes = mem, Count = procs.Count,
                HasWindow = hasWindow, Suggested = suggested, Selected = suggested, Pids = procs.Select(p => p.Id).ToList(),
            });
        }
        return result.OrderByDescending(a => a.Suggested).ThenByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>Nome do programa pelo .desktop instalado (ex.: "firefox" → "Firefox"); senão, o nome do processo.</summary>
    private static string? Title(Process p)
    {
        foreach (var dir in new[] { "/usr/share/applications", Path.Combine(Sys.Home, ".local/share/applications"), "/var/lib/flatpak/exports/share/applications" })
        {
            string f = Path.Combine(dir, p.ProcessName + ".desktop");
            if (File.Exists(f) && (Sys.Read(f) ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("Name=")) is string n) return n[5..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Fecha os programas: com janela → pede ao compositor para fechar (como clicar no X), sem forçar, para não perder nada
    /// não salvo; sem janela (segundo plano) → SIGTERM.
    /// </summary>
    public static CloseResult CloseApps(IEnumerable<BackgroundApp> apps)
    {
        var closed = new List<string>();
        var notClosed = new List<string>();
        foreach (var app in apps)
        {
            foreach (int pid in app.Pids)
            {
                if (app.HasWindow && Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") != null)
                    Sys.RunAsync("hyprctl", "dispatch", "closewindow", $"pid:{pid}").GetAwaiter().GetResult();
                else Sys.RunAsync("kill", "-TERM", pid.ToString()).GetAwaiter().GetResult();
            }
            // Dá até 4 s para fecharem sozinhos.
            var sw = Stopwatch.StartNew();
            bool Alive() => app.Pids.Any(pid => Directory.Exists($"/proc/{pid}"));
            while (sw.Elapsed.TotalSeconds < 4 && Alive()) Thread.Sleep(200);
            (Alive() ? notClosed : closed).Add(app.Title);
        }
        return new CloseResult(closed, notClosed);
    }

    /// <summary>
    /// Devolve ao sistema o cache de disco guardado na RAM (drop_caches), como a limpeza da "lista de espera" do Windows.
    /// O kernel já libera esse cache sozinho quando precisa; ajuda quando a RAM está quase cheia e o jogo engasga ao carregar.
    /// </summary>
    public static async Task<CleanupResult> CleanMemory()
    {
        long before = FreeBytes();
        var r = await Sys.RunRootAsync("sync\necho 1 > /proc/sys/vm/drop_caches\necho 1 > /proc/sys/vm/compact_memory 2>/dev/null || true\n");
        await Task.Delay(300);
        return new CleanupResult(Math.Max(0, FreeBytes() - before), r.Ok, r.Ok ? null : Sys.RootError(r));
    }

    private static long FreeBytes() => (long)(LinuxSensors.ParseMeminfo(Sys.Read("/proc/meminfo") ?? "").GetValueOrDefault("MemFree") * 1024);

    public static (long Total, long Available) MemoryStatus()
    {
        var m = LinuxSensors.ParseMeminfo(Sys.Read("/proc/meminfo") ?? "");
        return ((long)(m.GetValueOrDefault("MemTotal") * 1024), (long)(m.GetValueOrDefault("MemAvailable") * 1024));
    }
}
