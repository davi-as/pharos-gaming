using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PharosGaming;

/// <summary>App do catálogo "Instalar apps": pacote da distro (pacman/apt/dnf) ou Flatpak (Flathub) quando não houver.</summary>
public sealed class InstallableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Pacman { get; init; }
    public string? Apt { get; init; }
    public string? Dnf { get; init; }
    public string? Flatpak { get; init; }
    public bool PreSelect { get; init; } = true;

    private bool _selected, _installed;
    private string _status = "", _via = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Installed { get => _installed; set { Set(ref _installed, value); OnChanged(nameof(CanSelect)); } }
    public bool CanSelect => !Installed && Via.Length > 0;
    public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>Como seria instalado nesta máquina ("pacman", "Flatpak"...); vazio = indisponível.</summary>
    public string Via { get => _via; set { Set(ref _via, value); OnChanged(nameof(CanSelect)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>
/// Catálogo de apps essenciais (pós-formatação) e instalação pelo gerenciador de pacotes da distro, que baixa dos
/// repositórios oficiais e confere as assinaturas. O que não está nos repositórios vem do Flathub, sem root.
/// </summary>
public static class AppInstaller
{
    public static List<InstallableApp> Catalog() => new()
    {
        new() { Name = "Steam", Description = "Loja e biblioteca de jogos da Valve (com Proton para jogos de Windows).",
                Pacman = "steam", Apt = "steam-installer", Dnf = "steam", Flatpak = "com.valvesoftware.Steam" },
        new() { Name = "MangoHud", Description = "Overlay e log de FPS: é dele que o painel do PHAROS lê o FPS dos jogos.",
                Pacman = "mangohud lib32-mangohud", Apt = "mangohud", Dnf = "mangohud" },
        new() { Name = "GameMode", Description = "Coloca a CPU em desempenho máximo enquanto o jogo roda (opção da Biblioteca).",
                Pacman = "gamemode lib32-gamemode", Apt = "gamemode", Dnf = "gamemode" },
        new() { Name = "Discord", Description = "Chat de voz e texto para jogar com os amigos.", Pacman = "discord", Flatpak = "com.discordapp.Discord" },
        new() { Name = "Google Chrome", Description = "Navegador do Google.", Flatpak = "com.google.Chrome" },
        new() { Name = "Flameshot", Description = "Captura de tela rápida com edição (no lugar do Lightshot).",
                Pacman = "flameshot", Apt = "flameshot", Dnf = "flameshot", Flatpak = "org.flameshot.Flameshot" },
        new() { Name = "7-Zip", Description = "Compactador gratuito (zip, rar, 7z). Abre e cria arquivos compactados.", Pacman = "7zip", Apt = "7zip", Dnf = "7zip" },
        new() { Name = "unrar", Description = "Extrai arquivos .rar (no lugar do WinRAR). O 7-Zip já abre a maioria.", Pacman = "unrar", Apt = "unrar", Dnf = "unrar", PreSelect = false },
        new() { Name = "VLC", Description = "Player de vídeo e música que abre qualquer formato.", Pacman = "vlc", Apt = "vlc", Dnf = "vlc", Flatpak = "org.videolan.VLC" },
        new() { Name = "Lutris", Description = "Jogos de fora da Steam (Battle.net, EA, Ubisoft, emuladores) com Wine/Proton.",
                Pacman = "lutris", Apt = "lutris", Dnf = "lutris", Flatpak = "net.lutris.Lutris", PreSelect = false },
        new() { Name = "Heroic Games Launcher", Description = "Biblioteca da Epic Games, GOG e Amazon Prime Gaming.", Flatpak = "com.heroicgameslauncher.hgl", PreSelect = false },
        new() { Name = "ProtonUp-Qt", Description = "Instala o Proton-GE e outras versões do Proton para jogos que não rodam no oficial.", Flatpak = "net.davidotek.pupgui2", PreSelect = false },
        new() { Name = "OBS Studio", Description = "Gravação e transmissão de jogos.", Pacman = "obs-studio", Apt = "obs-studio", Dnf = "obs-studio", Flatpak = "com.obsproject.Studio", PreSelect = false },
        new() { Name = "Arduino IDE", Description = "Programar Arduino, ESP32 e outras placas (página Maker).", Pacman = "arduino-ide", Flatpak = "cc.arduino.IDE2", PreSelect = false },
        new() { Name = "smartmontools", Description = "Leitura completa do S.M.A.R.T. na página Discos (desgaste e total gravado).",
                Pacman = "smartmontools", Apt = "smartmontools", Dnf = "smartmontools", PreSelect = false },
    };

    /// <summary>Gerenciador de pacotes da distro: pacman, apt ou dnf (null = nenhum conhecido).</summary>
    public static string? Manager() => Sys.Has("pacman") ? "pacman" : Sys.Has("apt-get") ? "apt" : Sys.Has("dnf") ? "dnf" : null;

    private static string? Native(InstallableApp a, string? mgr) => mgr switch { "pacman" => a.Pacman, "apt" => a.Apt, "dnf" => a.Dnf, _ => null };

    /// <summary>Preenche Via e Installed de cada app (pacote nativo primeiro; senão Flatpak).</summary>
    public static async Task Detect(List<InstallableApp> apps)
    {
        string? mgr = Manager();
        bool flatpak = Sys.Has("flatpak");
        var flatpaks = flatpak ? (await Sys.RunAsync("flatpak", "list", "--app", "--columns=application")).Out.Split('\n').Select(s => s.Trim()).ToHashSet() : new HashSet<string>();
        foreach (var a in apps)
        {
            string? pkg = Native(a, mgr);
            bool nativeOk = pkg != null && await NativeInstalled(mgr!, pkg.Split(' ')[0]);
            bool fpOk = a.Flatpak != null && flatpaks.Contains(a.Flatpak);
            a.Installed = nativeOk || fpOk;
            a.Via = pkg != null ? mgr! : a.Flatpak != null ? (flatpak ? "Flatpak" : "Flatpak (instale o flatpak)") : "";
            if (a.Via.StartsWith("Flatpak (")) a.Via = "";
            a.Status = a.Installed ? "Já instalado" + (fpOk && !nativeOk ? " (Flatpak)" : "")
                     : a.Via.Length == 0 ? (a.Flatpak != null ? "Precisa do Flatpak (pacote flatpak da sua distro)" : "Não disponível nesta distro")
                     : $"via {a.Via}";
            a.Selected = !a.Installed && a.CanSelect && a.PreSelect;
        }
    }

    private static async Task<bool> NativeInstalled(string mgr, string pkg) => mgr switch
    {
        "pacman" => (await Sys.RunAsync("pacman", "-Q", pkg)).Ok,
        "apt" => (await Sys.RunAsync("dpkg-query", "-W", "-f=${Status}", pkg)).Out.Contains("install ok installed"),
        "dnf" => (await Sys.RunAsync("rpm", "-q", pkg)).Ok,
        _ => false,
    };

    /// <summary>Script de root com todos os pacotes nativos escolhidos numa transação só (uma senha).</summary>
    public static string NativeScript(string mgr, IEnumerable<string> packages)
    {
        string pkgs = string.Join(" ", packages.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().Select(Sys.Quote));
        return mgr switch
        {
            "pacman" => $"pacman -S --needed --noconfirm {pkgs}\n",
            "apt" => $"export DEBIAN_FRONTEND=noninteractive\napt-get update -q\napt-get install -y {pkgs}\n",
            "dnf" => $"dnf install -y {pkgs}\n",
            _ => throw new InvalidOperationException("Gerenciador de pacotes desconhecido."),
        };
    }

    /// <summary>Instala os escolhidos: primeiro os nativos (uma senha), depois os Flatpaks (sem root, só para o usuário).</summary>
    public static async Task Install(List<InstallableApp> apps, Action<string> log, CancellationToken ct)
    {
        string? mgr = Manager();
        var native = apps.Where(a => a.Via == mgr && Native(a, mgr) != null).ToList();
        var flat = apps.Where(a => a.Via == "Flatpak").ToList();
        if (native.Count > 0)
        {
            foreach (var a in native) a.Status = "Instalando…";
            log($"Instalando pelo {mgr}: {string.Join(", ", native.Select(a => a.Name))} (pede a senha)");
            var r = await Sys.RunRootAsync(NativeScript(mgr!, native.Select(a => Native(a, mgr)!)), ct);
            foreach (var line in (r.Out + r.Err).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6)) log("  " + line.Trim());
            foreach (var a in native) a.Status = r.Ok ? "Instalado" : "Falhou: " + Sys.RootError(r);
        }
        if (flat.Count > 0)
        {
            await Sys.RunAsync("flatpak", new[] { "remote-add", "--user", "--if-not-exists", "flathub", "https://dl.flathub.org/repo/flathub.flatpakrepo" }, ct);
            foreach (var a in flat)
            {
                ct.ThrowIfCancellationRequested();
                a.Status = "Instalando…";
                log($"Instalando {a.Name} pelo Flatpak ({a.Flatpak})");
                var r = await Sys.RunAsync("flatpak", new[] { "install", "--user", "-y", "--noninteractive", "flathub", a.Flatpak! }, ct);
                a.Status = r.Ok ? "Instalado" : "Falhou: " + (r.Err.Trim().Split('\n').LastOrDefault() ?? $"código {r.Code}");
            }
        }
        await Detect(apps);
    }
}
