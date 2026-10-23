using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>
/// Monta a linha de inicialização do perfil e inicia o jogo. Nada fica gravado no sistema: MangoHud e GameMode valem só
/// para o processo do jogo, e os programas auxiliares fecham junto com ele.
/// </summary>
public static class GameLauncher
{
    // ---------------- Argumentos ----------------

    /// <summary>Monta a linha de argumentos do perfil (modo de janela, resolução, taxa e extras) no estilo da engine escolhida.</summary>
    public static string BuildArgs(GameProfile p)
    {
        var a = new List<string>();
        int w = 0, h = 0;
        var m = Regex.Match(p.Res ?? "", @"^(\d+)x(\d+)$");
        if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }

        switch (p.Style)
        {
            case "unity":
                if (p.WinMode == "full") a.Add("-screen-fullscreen 1");
                else if (p.WinMode == "window") a.Add("-screen-fullscreen 0");
                else if (p.WinMode == "borderless") a.Add("-popupwindow");
                if (w > 0) a.Add($"-screen-width {w} -screen-height {h}");
                break;
            case "unreal":
                if (p.WinMode == "full") a.Add("-fullscreen");
                else if (p.WinMode is "window" or "borderless") a.Add("-windowed");
                if (w > 0) a.Add($"-ResX={w} -ResY={h}");
                break;
            default: // source / CS2
                if (p.WinMode == "full") a.Add("-fullscreen");
                else if (p.WinMode == "window") a.Add("-windowed");
                else if (p.WinMode == "borderless") a.Add("-windowed -noborder");
                if (w > 0) a.Add($"-w {w} -h {h}");
                if (p.Hz > 0) a.Add($"-freq {p.Hz}");
                break;
        }
        if (!string.IsNullOrWhiteSpace(p.Extra)) a.Add(p.Extra.Trim());
        return string.Join(" ", a);
    }

    /// <summary>Linha no formato das opções de inicialização da Steam: MangoHud e GameMode antes de %command%, argumentos depois.</summary>
    public static string LaunchLine(GameProfile p)
    {
        var parts = Wrappers(p).Select(w => w == "mangohud" ? $"{MangoHudConfig.EnvPrefix}MANGOHUD_CONFIG=\"{MangoHudFrames.LogConfig}\" mangohud" : w).ToList();
        string args = BuildArgs(p);
        if (parts.Count == 0) return args; // a Steam aceita só os argumentos
        parts.Add("%command%");
        if (args.Length > 0) parts.Add(args);
        return string.Join(" ", parts);
    }

    private static List<string> Wrappers(GameProfile p)
    {
        var w = new List<string>();
        if (p.MangoHud) w.Add("mangohud");
        if (p.GameMode) w.Add("gamemoderun");
        return w;
    }

    public static bool HasCommand(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, name)));

    // ---------------- Processo do jogo ----------------

    /// <summary>Jogo da Steam rodando: o "reaper" da Steam fica vivo enquanto o jogo roda e leva "AppId=N" na linha de comando.</summary>
    private static int? FindSteamGame(string appId)
    {
        string token = "AppId=" + appId;
        foreach (var d in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(d), out int pid)) continue;
            try { if (File.ReadAllText(Path.Combine(d, "cmdline")).Split('\0').Contains(token)) return pid; } catch { }
        }
        return null;
    }

    private static async Task WaitPidAsync(int pid, CancellationToken ct)
    {
        while (Directory.Exists($"/proc/{pid}")) await Task.Delay(1000, ct);
    }

    private static void Close(Process proc)
    {
        try
        {
            if (proc.HasExited) return;
            Process.Start("kill", new[] { "-TERM", proc.Id.ToString() })?.WaitForExit(1000); // pede para fechar antes de forçar
            if (!proc.WaitForExit(1500)) proc.Kill();
        }
        catch { }
    }

    // ---------------- Iniciar ----------------

    /// <summary>Abre os auxiliares, inicia o jogo e acompanha até ele fechar. Ao terminar (ou cancelar) fecha os auxiliares marcados.</summary>
    public static async Task RunAsync(LibGame g, GameProfile p, Action<string> status, CancellationToken ct)
    {
        var helpers = new List<(Process Proc, bool Close)>();
        try
        {
            foreach (var h in p.Helpers.Where(h => File.Exists(h.Path)))
            {
                status($"Abrindo {Path.GetFileName(h.Path)}…");
                try
                {
                    var proc = Process.Start(new ProcessStartInfo(h.Path, h.Args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(h.Path) ?? "" });
                    if (proc != null) helpers.Add((proc, h.CloseWithGame));
                }
                catch { }
            }
            if (helpers.Count > 0) await Task.Delay(1500, ct);

            status($"Iniciando {g.Name}…");
            if (g.IsSteam)
            {
                Process.Start(new ProcessStartInfo($"steam://rungameid/{g.Id}") { UseShellExecute = true })?.Dispose();
                int? pid = null;
                for (int i = 0; i < 120 && pid == null; i++) // até 3 min (a Steam pode estar abrindo ou atualizando)
                {
                    await Task.Delay(1500, ct);
                    pid = FindSteamGame(g.Id);
                }
                if (pid == null) { status("Não detectei o jogo (ele pode ter demorado a abrir)."); return; }
                status($"{g.Name} em execução.");
                await WaitPidAsync(pid.Value, ct);
            }
            else
            {
                if (g.ExePath == null || !File.Exists(g.ExePath)) throw new InvalidOperationException("O executável deste jogo não existe mais.");
                var cmd = Wrappers(p);
                cmd.Add(g.ExePath);
                var psi = new ProcessStartInfo(cmd[0]) { UseShellExecute = false, WorkingDirectory = g.InstallDir };
                foreach (var c in cmd.Skip(1)) psi.ArgumentList.Add(c);
                // shortcut: argumentos separados por espaço, sem aspas; trocar por um parser se algum jogo precisar de espaços num argumento
                foreach (var a in (g.Args + " " + BuildArgs(p)).Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
                if (p.MangoHud)
                {
                    psi.Environment["MANGOHUD_CONFIG"] = MangoHudFrames.LogConfig;
                    if (AppSettings.Current.OverlayEnabled) psi.Environment["MANGOHUD_CONFIGFILE"] = MangoHudConfig.FilePath;
                }
                using var game = Process.Start(psi)!;
                status($"{g.Name} em execução.");
                await game.WaitForExitAsync(ct);
            }
            status($"{g.Name} encerrado.");
        }
        catch (OperationCanceledException) { status("Acompanhamento cancelado."); }
        catch (Exception ex) { status("Falhou: " + ex.Message); }
        finally
        {
            foreach (var (proc, close) in helpers)
            {
                if (close) Close(proc);
                proc.Dispose();
            }
        }
    }
}
