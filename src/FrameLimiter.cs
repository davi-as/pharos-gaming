using System.Globalization;
using System.Text.Json;

namespace PharosGaming;

/// <summary>Jogo com o MangoHud carregado agora. Key = nome do arquivo de config por jogo que o MangoHud procura (sem ".conf").</summary>
public sealed record RunningGame(int Pid, string Key)
{
    public string Name => FrameLimiter.Display(Key);
}

/// <summary>
/// Limitador de FPS com frame pacing (a ideia do framepacer, no Linux): o próprio limitador do MangoHud no modo early,
/// que segura cada quadro até a hora exata e entrega tempos de quadro iguais.
/// O limite vai num arquivo por jogo (~/.config/MangoHud/wine-JOGO.conf), que o MangoHud prefere ao MangoHud.conf
/// e relê sozinho quando muda: vale para jogos abertos por qualquer launcher (Hydra, Steam) e muda com o jogo aberto.
/// </summary>
public static class FrameLimiter
{
    public static string Dir => Path.Combine(Sys.ConfigHome, "MangoHud");
    private static string UserConf => Path.Combine(Dir, "MangoHud.conf");
    public static string FileFor(string key) => Path.Combine(Dir, key + ".conf");

    private const string Header = "# Gerado pelo PHAROS GAMING (Limitador de FPS).";
    public const string ToggleKeys = "Shift_L+F1"; // padrão do MangoHud: alterna entre o limite e sem limite

    // opções que o arquivo por jogo define; as do arquivo base com essas chaves saem
    private static readonly HashSet<string> Owned = new()
        { "fps_limit", "fps_limit_method", "toggle_fps_limit", "show_fps_limit", "autostart_log", "log_interval", "output_folder" };

    /// <summary>"wine-eldenring" → "eldenring".</summary>
    public static string Display(string key) => key.StartsWith("wine-") ? key[5..] : key;

    /// <summary>
    /// Mesmo nome que o MangoHud 0.8 usa (config.cpp/file_utils.cpp): no Wine, "wine-" + o .exe (pelo comm ou pela
    /// linha de comando); fora dele, o nome do executável.
    /// </summary>
    public static string? KeyFor(string exePath, string comm, IEnumerable<string> args)
    {
        if (exePath.EndsWith("wine-preloader") || exePath.EndsWith("wine64-preloader"))
        {
            if (comm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "wine-" + comm[..comm.LastIndexOf('.')];
            foreach (var a in args)
            {
                int n = a.LastIndexOfAny(new[] { '/', '\\' });
                if (a.Length == 0 || n < 0 || n >= a.Length - 1) continue;
                int dot = a.LastIndexOf('.');
                if (dot < n) dot = a.Length;
                return "wine-" + a[(n + 1)..dot];
            }
        }
        string name = Path.GetFileName(exePath);
        return name.Length > 0 ? name : null;
    }

    /// <summary>Processos com o MangoHud carregado, tirando os do Electron/CEF (o Hydra também carrega o MangoHud na janela dele).</summary>
    public static List<RunningGame> Running()
    {
        var found = new List<RunningGame>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid) || pid == Environment.ProcessId) continue;
            try
            {
                // environ é pequeno: filtra antes de ler o maps, que é grande
                if (!File.ReadAllText(Path.Combine(dir, "environ")).Contains("MANGOHUD")) continue;
                var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (args.Any(a => a.StartsWith("--type="))) continue;
                if (!File.ReadLines(Path.Combine(dir, "maps")).Any(l => l.Contains("/libMangoHud"))) continue;
                string exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget ?? "";
                if (KeyFor(exe, (Sys.Read(Path.Combine(dir, "comm")) ?? "").Trim(), args) is { } key) found.Add(new RunningGame(pid, key));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.DistinctBy(g => g.Key).ToList();
    }

    /// <summary>
    /// O jogo entre os processos com MangoHud: o que tem janela (o explorer.exe /desktop do Wine também carrega o
    /// MangoHud, sem janela); sem lista de janelas, o primeiro fora da lista negra do MangoHud.
    /// </summary>
    public static RunningGame? Pick(IEnumerable<RunningGame> games, ISet<int> windowPids)
    {
        var real = games.Where(g => g.Key != "wine-explorer").ToList();
        return real.FirstOrDefault(g => windowPids.Contains(g.Pid)) ?? real.FirstOrDefault();
    }

    /// <summary>PIDs com janela no Hyprland (vazio fora dele).</summary>
    public static HashSet<int> WindowPids()
    {
        try
        {
            using var clients = JsonDocument.Parse(Sys.RunAsync("hyprctl", "clients", "-j").GetAwaiter().GetResult().Out);
            return clients.RootElement.EnumerateArray().Select(c => c.GetProperty("pid").GetInt32()).ToHashSet();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>Config do jogo: a base inteira (o arquivo é lido no lugar dela, não junto) + o limite e o log que o PHAROS lê.</summary>
    public static string Build(string key, double limit, IEnumerable<string> baseLines)
    {
        var lines = new List<string> { $"{Header} Jogo: {Display(key)}. Edições manuais são substituídas." };
        lines.AddRange(baseLines.Where(l => !l.StartsWith("# Gerado pelo PHAROS") && !Owned.Contains(l.Split('=', 2)[0].Trim())));
        string fps = limit.ToString("0.##", CultureInfo.InvariantCulture);
        lines.AddRange(new[]
        {
            limit > 0 ? $"fps_limit={fps},0" : "fps_limit=0",
            "fps_limit_method=early",          // tempos de quadro iguais (late = menos atraso, pacing pior)
            $"toggle_fps_limit={ToggleKeys}",
            "show_fps_limit",
            "autostart_log=1", "log_interval=250", $"output_folder={MangoHudFrames.OwnFolder}", // o painel do PHAROS lê esse log
        });
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Base: o MangoHud.conf (o do usuário, ou o do PHAROS quando o overlay dele está ligado) — o que o jogo usaria sem o arquivo por jogo.</summary>
    private static IEnumerable<string> BaseLines()
    {
        try { return File.ReadAllLines(UserConf); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>Grava o limite (0 = sem limite) do jogo e salva nas configurações.</summary>
    public static void Set(AppSettings s, string key, double limit)
    {
        s.FpsLimits[key] = limit;
        s.Save();
        Write(s, key);
    }

    public static void Remove(AppSettings s, string key)
    {
        s.FpsLimits.Remove(key);
        s.Save();
        string f = FileFor(key);
        if (IsOurs(f)) File.Delete(f);
        if (File.Exists(f + ".pharos-bak")) File.Move(f + ".pharos-bak", f); // devolve a config que já existia
    }

    /// <summary>Regrava todos (o overlay do PHAROS ou o MangoHud.conf do usuário mudou).</summary>
    public static void RewriteAll(AppSettings s)
    {
        foreach (var key in s.FpsLimits.Keys) Write(s, key);
    }

    private static bool IsOurs(string f) => File.Exists(f) && File.ReadLines(f).FirstOrDefault()?.StartsWith(Header) == true;

    private static void Write(AppSettings s, string key)
    {
        Directory.CreateDirectory(Dir);
        string f = FileFor(key);
        bool existed = File.Exists(f);
        if (existed && !IsOurs(f) && !File.Exists(f + ".pharos-bak")) File.Copy(f, f + ".pharos-bak"); // config por jogo feita à mão: guarda antes
        // troca atômica: o MangoHud relê no IN_DELETE_SELF (espera 100 ms pelo arquivo novo) e nunca vê o arquivo pela metade
        File.WriteAllText(f + ".tmp", Build(key, s.FpsLimits[key], BaseLines()));
        File.Move(f + ".tmp", f, overwrite: true);
        if (!existed) Poke();
    }

    /// <summary>
    /// Um jogo já aberto vigia o MangoHud.conf, não o arquivo novo. Regravar um byte dele (o mesmo) gera o IN_MODIFY,
    /// o MangoHud procura a config de novo, acha a do jogo e passa a vigiá-la.
    /// </summary>
    private static void Poke()
    {
        try
        {
            using var fs = new FileStream(UserConf, FileMode.Open, FileAccess.ReadWrite);
            int b = fs.ReadByte();
            if (b < 0) return;
            fs.Position = 0;
            fs.WriteByte((byte)b);
        }
        catch (IOException) { } // sem MangoHud.conf o jogo aberto não vigia nada: vale na próxima vez que abrir
    }

    // ---------------- sugestões ----------------

    public sealed record Screen(string Name, double Hz, bool Vrr);

    /// <summary>Tela onde está a janela do jogo (hyprctl); sem Hyprland ou sem janela, a tela em foco.</summary>
    public static Screen? ScreenOf(int pid)
    {
        try
        {
            using var mons = JsonDocument.Parse(Sys.RunAsync("hyprctl", "monitors", "-j").GetAwaiter().GetResult().Out);
            using var clients = JsonDocument.Parse(Sys.RunAsync("hyprctl", "clients", "-j").GetAwaiter().GetResult().Out);
            int? monId = clients.RootElement.EnumerateArray().Where(c => c.GetProperty("pid").GetInt32() == pid)
                .Select(c => (int?)c.GetProperty("monitor").GetInt32()).FirstOrDefault();
            var m = mons.RootElement.EnumerateArray().FirstOrDefault(m => monId is { } id ? m.GetProperty("id").GetInt32() == id : m.GetProperty("focused").GetBoolean());
            if (m.ValueKind != JsonValueKind.Object) return null;
            return new Screen(m.GetProperty("name").GetString() ?? "", m.GetProperty("refreshRate").GetDouble(), m.GetProperty("vrr").GetBoolean());
        }
        catch (Exception) { return null; } // sem hyprctl (outro desktop) ou JSON inesperado: só não sugere pela tela
    }

    /// <summary>
    /// Limites que casam com a tela. Sem VRR, só divisores da taxa (165 → 165, 82.5, 55, 41.25): cada quadro fica o mesmo
    /// número de atualizações na tela. Com VRR (G-Sync/FreeSync) qualquer valor um pouco abaixo da taxa serve.
    /// </summary>
    public static List<double> Candidates(double hz, bool vrr)
    {
        if (vrr) return new[] { 30.0, 40, 45, 60, 72, 90, 100, 120, 144, 165, 200, 240 }.Where(v => v <= hz - 3).Reverse().ToList();
        return Enumerable.Range(1, 6).Select(n => Math.Round(hz / n, 2)).Where(v => v >= 29).ToList();
    }

    /// <summary>Maior limite da lista que o PC segura com folga (pelo mínimo medido); null se nenhum.</summary>
    public static double? Suggest(IEnumerable<double> candidates, double minFps) =>
        candidates.Where(c => c <= minFps * 0.97).Cast<double?>().Max();

    // ---------------- veredito ----------------

    public sealed record Verdict(string Title, string Detail, bool Good);

    /// <summary>Lê os tempos de quadro do log (amostras de 250 ms) contra o limite pedido.</summary>
    public static Verdict? Judge(IReadOnlyList<double> frameTimesMs, double limit)
    {
        if (frameTimesMs.Count < 8) return null;
        double avg = frameTimesMs.Average();
        double sd = Math.Sqrt(frameTimesMs.Sum(t => (t - avg) * (t - avg)) / frameTimesMs.Count);
        double fps = 1000 / avg;
        if (limit > 0 && fps < limit * 0.97)
            return new Verdict("Abaixo do limite",
                $"O jogo está em {fps:0} FPS e o limite é {limit:0.#}: o PC não segura esse valor nesta cena. Baixe o limite ou os gráficos, senão o limitador não tem o que regular.", false);
        if (sd <= 1)
            return new Verdict("Ritmo estável", $"Quadros a cada {avg:0.0} ms, variando ±{sd:0.0} ms.", true);
        if (sd <= 3)
            return new Verdict("Pequenas oscilações", $"Quadros a cada {avg:0.0} ms, variando ±{sd:0.0} ms. Um limite um degrau abaixo deixa mais liso.", false);
        return new Verdict("Ritmo irregular", $"Quadros variando ±{sd:0.0} ms em torno de {avg:0.0} ms: isso é o engasgo que se sente. "
            + (limit > 0 ? "Baixe o limite um degrau." : "Defina um limite que o PC segure."), false);
    }
}
