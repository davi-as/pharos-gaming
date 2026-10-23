namespace PharosGaming;

/// <summary>
/// Geração de quadros (lsfg-vk, a da Lossless Scaling no Linux). O PHAROS só escreve a config dele
/// (~/.config/lsfg-vk/conf.toml): um perfil por jogo ligado, reconhecido pelo .exe. Multiplicador muda com o jogo
/// aberto (hot reload do lsfg-vk). Sem VRR, o total precisa bater com a tela: com 2× o limite real vira metade (29,98).
/// </summary>
public static class Lsfg
{
    public static string ConfPath => Path.Combine(Sys.ConfigHome, "lsfg-vk", "conf.toml");
    /// <summary>Cópia na pasta pessoal: o runtime da Steam pode esconder outras partições do jogo.</summary>
    public static string DllPath => Path.Combine(Sys.Home, ".local", "share", "pharos-gaming", "lsfg-vk.dll");
    private const string Header = "# Gerado pelo PHAROS GAMING (geração de quadros por jogo). Edições manuais são substituídas.";

    public static bool Installed =>
        File.Exists(Path.Combine(Sys.Home, ".local", "lib", "liblsfg-vk-layer.so")) || File.Exists("/usr/lib/liblsfg-vk-layer.so");

    /// <summary>O lsfg-vk.dll vem no ramo beta "lsfg-vk" da Lossless Scaling, na Steam.</summary>
    public static string? DllSource()
    {
        if (SteamInfo.SteamPath() is not { } steam) return null;
        return SteamInfo.LibraryFolders(steam).Select(l => Path.Combine(l, "steamapps", "common", "Lossless Scaling", "lsfg-vk.dll")).FirstOrDefault(File.Exists);
    }

    /// <summary>null quando dá para ligar; senão, o que falta (em palavras do usuário).</summary>
    public static string? Missing()
    {
        if (!Installed) return "O lsfg-vk não está instalado.";
        if (!File.Exists(DllPath) && DllSource() == null)
            return "Falta o arquivo da Lossless Scaling: na Steam, Lossless Scaling › Propriedades › Betas › lsfg-vk.";
        return null;
    }

    /// <summary>wine-eldenring → eldenring.exe (o lsfg-vk reconhece o executável do Windows); nativo: o binário.</summary>
    public static string ExeFor(string key) => key.StartsWith("wine-") ? key[5..] + ".exe" : key;

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static string Build(IReadOnlyDictionary<string, int> multipliers, string dll)
    {
        var lines = new List<string> { Header, "version = 2", "", "[global]", $"dll = {Q(dll)}" };
        foreach (var (key, m) in multipliers.Where(p => p.Value >= 2).OrderBy(p => p.Key))
            lines.AddRange(new[] { "", "[[profile]]", $"name = {Q(FrameLimiter.Display(key))}", $"active_in = [ {Q(ExeFor(key))} ]", $"multiplier = {m}" });
        return string.Join("\n", lines) + "\n";
    }

    public static int Multiplier(AppSettings s, string key) => s.FgMultiplier.GetValueOrDefault(key, 1);

    /// <summary>Liga (2×) ou desliga para o jogo; o limite real acompanha para o total bater com a tela.</summary>
    public static void Set(AppSettings s, string key, int multiplier, double screenHz)
    {
        if (multiplier >= 2) s.FgMultiplier[key] = multiplier; else s.FgMultiplier.Remove(key);
        if (DllSource() is { } src && (!File.Exists(DllPath) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(DllPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DllPath)!);
            File.Copy(src, DllPath, overwrite: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ConfPath)!);
        if (File.Exists(ConfPath) && !File.ReadLines(ConfPath).FirstOrDefault()?.StartsWith("# Gerado pelo PHAROS") == true && !File.Exists(ConfPath + ".pharos-bak"))
            File.Copy(ConfPath, ConfPath + ".pharos-bak"); // config feita à mão (lsfg-vk-ui): guarda antes
        File.WriteAllText(ConfPath + ".tmp", Build(s.FgMultiplier, DllPath));
        File.Move(ConfPath + ".tmp", ConfPath, overwrite: true);
        FrameLimiter.Set(s, key, Math.Round(screenHz / Math.Max(1, multiplier), 2)); // 59,95 sem; 29,98 com 2×
    }
}
