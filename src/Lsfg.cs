namespace PharosGaming;

/// <summary>
/// Geração de quadros (lsfg-vk, a da Lossless Scaling no Linux). O PHAROS só escreve a config dele
/// (~/.config/lsfg-vk/conf.toml): um perfil por jogo ligado, reconhecido pelo .exe. O lsfg-vk escolhe o perfil quando o
/// jogo abre (testado: ligar ou desligar com o jogo aberto não pega; só o multiplicador muda ao vivo), então a troca
/// vale ao reabrir. Sem VRR o total bate com a tela: com 2× o limite real vira metade (29,98), aplicado com o jogo fechado.
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
        var on = multipliers.Where(p => p.Value >= 2).OrderBy(p => p.Key).ToList();
        foreach (var (key, m) in on)
            lines.AddRange(new[] { "", "[[profile]]", $"name = {Q(FrameLimiter.Display(key))}", $"active_in = [ {Q(ExeFor(key))} ]", $"multiplier = {m}" });
        // sem nenhum perfil o lsfg-vk acha a config inválida e segue com a anterior: um que não casa com nada
        if (on.Count == 0) lines.AddRange(new[] { "", "[[profile]]", "name = \"nenhum jogo\"", "active_in = [ \"pharos-nenhum-jogo\" ]" });
        return string.Join("\n", lines) + "\n";
    }

    public static int Multiplier(AppSettings s, string key) => s.FgMultiplier.GetValueOrDefault(key, 1);

    /// <summary>Liga (2×) ou desliga para o jogo. Vale ao reabrir; o limite acompanha quando o jogo fechar (Settle).</summary>
    public static void Set(AppSettings s, string key, int multiplier, double screenHz)
    {
        s.FgScreenHz[key] = screenHz;
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
        s.Save();
    }

    /// <summary>Pendente: o jogo pediu outra geração, mas o limite ainda é o da antiga.</summary>
    public static bool Pending(AppSettings s, string key) => Multiplier(s, key) != s.FgApplied.GetValueOrDefault(key, 1);

    /// <summary>
    /// Com o jogo fechado, o limite passa ao da geração pedida (59,95 sem; 29,98 com 2×). Chamado em segundo plano com os
    /// jogos abertos agora; devolve as chaves ajustadas.
    /// </summary>
    public static List<string> Settle(AppSettings s, ISet<string> running)
    {
        var done = new List<string>();
        foreach (var key in s.FgMultiplier.Keys.Concat(s.FgApplied.Keys).Distinct().ToList())
        {
            if (running.Contains(key) || !Pending(s, key) || !s.FgScreenHz.TryGetValue(key, out double hz)) continue;
            int m = Multiplier(s, key);
            if (m >= 2) s.FgApplied[key] = m; else s.FgApplied.Remove(key);
            FrameLimiter.Set(s, key, Math.Round(hz / m, 2));
            done.Add(key);
        }
        return done;
    }
}
