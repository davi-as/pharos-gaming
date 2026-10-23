using System.Diagnostics;

namespace PharosGaming;

/// <summary>Jogo instalado na Steam ou adicionado à mão.</summary>
public sealed class LibGame
{
    public string Store { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string InstallDir { get; init; } = "";
    public string? ExePath { get; init; }
    public string Args { get; init; } = "";
    public string Key => Store + ":" + Id;
    public string Subtitle => Store;
    public bool IsSteam => Store == "Steam";
}

/// <summary>Procura jogos instalados na Steam e os adicionados manualmente. Só lê arquivos.</summary>
public static class GameLibrary
{
    public static List<LibGame> Scan(IEnumerable<CustomGame> custom)
    {
        var all = new List<LibGame>();
        try { all.AddRange(Steam()); } catch { }
        foreach (var c in custom)
        {
            if (!File.Exists(c.Exe)) continue;
            all.Add(new LibGame
            {
                Store = "Manual", Id = c.Exe, Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileNameWithoutExtension(c.Exe) : c.Name,
                InstallDir = Path.GetDirectoryName(c.Exe) ?? "", ExePath = c.Exe, Args = c.Args,
            });
        }
        return all.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<LibGame> Steam()
    {
        string? steam = SteamInfo.SteamPath();
        if (steam == null) yield break;
        foreach (var lib in SteamInfo.LibraryFolders(steam))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                LibGame? g = null;
                try { g = ParseManifest(File.ReadAllText(acf), apps); } catch { }
                if (g != null) yield return g;
            }
        }
    }

    internal static LibGame? ParseManifest(string acf, string apps)
    {
        var st = Vdf.Parse(acf).Child("AppState");
        if (st == null) return null;
        string? id = st.Get("appid"), name = st.Get("name"), dir = st.Get("installdir");
        if (id == null || name == null || dir == null) return null;
        if (long.TryParse(st.Get("StateFlags"), out long flags) && (flags & 4) == 0) return null; // 4 = instalado por completo
        if (id == "228980" || name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)) return null;
        string full = Path.Combine(apps, "common", dir);
        return Directory.Exists(full) ? new LibGame { Store = "Steam", Id = id, Name = name, InstallDir = full } : null;
    }
}

/// <summary>Informações da instalação da Steam e edição das "Opções de inicialização" (localconfig.vdf).</summary>
public static class SteamInfo
{
    public static string? SteamPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // nativa, link da nativa e Flatpak
        foreach (var rel in new[] { ".local/share/Steam", ".steam/steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam" })
        {
            string p = Path.Combine(home, rel);
            if (Directory.Exists(Path.Combine(p, "steamapps"))) return p;
        }
        return null;
    }

    public static List<string> LibraryFolders(string steam)
    {
        var list = new List<string> { steam };
        try
        {
            string f = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var root = File.Exists(f) ? Vdf.Parse(File.ReadAllText(f)).Child("libraryfolders") : null;
            if (root != null)
                foreach (var (_, c) in root.Children)
                    if (c.Get("path") is string p && Directory.Exists(p) && !list.Contains(p.TrimEnd('/'))) list.Add(p.TrimEnd('/'));
        }
        catch { }
        return list;
    }

    public static bool IsRunning => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>localconfig.vdf mais recente (a conta que usou a Steam por último).</summary>
    private static string? LocalConfig()
    {
        string? steam = SteamPath();
        if (steam == null) return null;
        string ud = Path.Combine(steam, "userdata");
        if (!Directory.Exists(ud)) return null;
        return Directory.GetDirectories(ud).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static VdfNode? AppsNode(VdfNode root, bool create)
    {
        VdfNode? n = root.Child("UserLocalConfigStore");
        foreach (var key in new[] { "Software", "Valve", "Steam", "apps" })
        {
            if (n == null) return null;
            n = create ? n.GetOrAddChild(key) : n.Child(key);
        }
        return n;
    }

    public static string? GetLaunchOptions(string appId)
    {
        try
        {
            string? f = LocalConfig();
            if (f == null) return null;
            return AppsNode(Vdf.Parse(File.ReadAllText(f)), false)?.Child(appId)?.Get("LaunchOptions");
        }
        catch { return null; }
    }

    /// <summary>Grava as opções de inicialização do jogo. A Steam precisa estar fechada (senão ela sobrescreve ao sair). Devolve null se deu certo, ou o erro.</summary>
    public static string? SetLaunchOptions(string appId, string options)
    {
        if (IsRunning) return "A Steam está aberta. Feche-a para gravar as opções (ela sobrescreve o arquivo ao sair).";
        string? f = LocalConfig();
        if (f == null) return "Não achei o localconfig.vdf da Steam (entre na Steam ao menos uma vez neste PC).";
        try
        {
            var root = Vdf.Parse(File.ReadAllText(f));
            var app = AppsNode(root, true)!.GetOrAddChild(appId);
            if (string.IsNullOrWhiteSpace(options)) app.Remove("LaunchOptions"); else app.Set("LaunchOptions", options.Trim());
            File.Copy(f, f + ".pharos.bak", true); // cópia de segurança do arquivo original
            File.WriteAllText(f, Vdf.Serialize(root), new System.Text.UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) { return "Não consegui gravar: " + ex.Message; }
    }

    /// <summary>Pede à Steam para fechar (steam -shutdown) e espera até 25 s.</summary>
    public static async Task<bool> ShutdownAsync()
    {
        try { Process.Start(new ProcessStartInfo("steam", "-shutdown") { UseShellExecute = false })?.Dispose(); } catch { }
        for (int i = 0; i < 50 && IsRunning; i++) await Task.Delay(500);
        return !IsRunning;
    }
}
