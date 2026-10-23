using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PharosGaming;

/// <summary>Resumo de uma sessão de jogo, tirado do log do MangoHud.</summary>
public sealed record GameSession(string Game, DateTime Start, double Minutes, double AvgFps, double Low1Fps, double PacingMs,
                                 double? CpuTempMax, double? GpuTempMax, double? CpuLoadAvg, double? GpuLoadAvg, double Limit,
                                 List<string> Files);

/// <summary>
/// Histórico de sessões: quando um CSV do MangoHud para de crescer (jogo fechado), vira um resumo em sessions.json.
/// Logs do mesmo jogo com menos de 3 min entre um e outro são uma sessão só (reler a config abre um log novo).
/// Os CSVs da pasta do PHAROS com mais de 14 dias são apagados depois de resumidos.
/// </summary>
public static partial class SessionLog
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "sessions.json");
    private const int MaxSessions = 500, MaxSeen = 3000;
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(20), Gap = TimeSpan.FromMinutes(3), KeepLogs = TimeSpan.FromDays(14);
    private static readonly object Gate = new();

    public sealed class Store
    {
        public List<GameSession> Sessions { get; set; } = new();
        /// <summary>CSVs já vistos (resumidos ou curtos demais), para não ler de novo.</summary>
        public List<string> Seen { get; set; } = new();
    }

    public static Store Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    private static void Save(Store s)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }

    public sealed record Row(double Fps, double FrameTime, double? CpuLoad, double? GpuLoad, double? CpuTemp, double? GpuTemp);

    /// <summary>Linhas de dados do CSV (depois do cabeçalho "fps,frametime,..."); MangoHud grava 0 quando não tem o sensor.</summary>
    public static List<Row> Parse(IEnumerable<string> lines)
    {
        var rows = new List<Row>();
        Dictionary<string, int>? col = null;
        foreach (var line in lines)
        {
            var c = line.Trim().Split(',');
            if (c[0] == "fps") { col = c.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i); continue; }
            if (col == null || !Num(c, col, "fps", out double fps) || fps <= 0) continue;
            double? Opt(string name) => Num(c, col, name, out double v) && v > 0 ? v : null;
            rows.Add(new Row(fps, Num(c, col, "frametime", out double ft) && ft > 0 ? ft : 1000 / fps,
                             Opt("cpu_load"), Opt("gpu_load"), Opt("cpu_temp"), Opt("gpu_temp")));
        }
        return rows;
    }

    private static bool Num(string[] c, Dictionary<string, int> col, string name, out double v)
    {
        v = 0;
        return col.TryGetValue(name, out int i) && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>
    /// Resumo. shortcut: as linhas são amostras de 250 ms, não cada quadro: o 1% low sai das amostras mais lentas
    /// (aproxima o de um benchmark); para o valor exato, log_interval=0 e arquivos bem maiores.
    /// </summary>
    public static GameSession? Summarize(string game, DateTime start, List<Row> rows, double minutes, double limit, List<string> files)
    {
        if (rows.Count < 20 || minutes < 1) return null; // abrir e fechar, testes: não é sessão
        var ft = rows.Select(r => r.FrameTime).ToList();
        double avgFt = ft.Average();
        int n = Math.Max(1, ft.Count / 100);
        double low1 = 1000 / ft.OrderByDescending(t => t).Take(n).Average();
        double sd = Math.Sqrt(ft.Sum(t => (t - avgFt) * (t - avgFt)) / ft.Count);
        static double? Max(IEnumerable<double?> v) => v.Where(x => x != null).DefaultIfEmpty(null).Max();
        static double? Avg(IEnumerable<double?> v) { var l = v.Where(x => x != null).ToList(); return l.Count > 0 ? l.Average() : null; }
        return new GameSession(game, start, minutes, rows.Average(r => r.Fps), low1, sd,
            Max(rows.Select(r => r.CpuTemp)), Max(rows.Select(r => r.GpuTemp)), Avg(rows.Select(r => r.CpuLoad)), Avg(rows.Select(r => r.GpuLoad)),
            limit, files);
    }

    /// <summary>"Hades_2026-10-23_12-17-57.csv" → ("Hades", 23/10/2026 12:17:57).</summary>
    public static (string Game, DateTime Start)? FromName(string file)
    {
        var m = NameRe().Match(Path.GetFileName(file));
        if (!m.Success) return null;
        string game = m.Groups[1].Value;
        if (game.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) game = game[..^4];
        return (game, DateTime.ParseExact(m.Groups[2].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^(.+)_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.csv$")] private static partial Regex NameRe();

    /// <summary>Lê os CSVs terminados que ainda não viraram sessão. Chamado em segundo plano a cada ~30 s.</summary>
    public static void Scan(IEnumerable<string> folders, string? ownFolder = null)
    {
        ownFolder ??= MangoHudFrames.OwnFolder;
        lock (Gate)
        {
            var store = Load();
            var seen = store.Seen.ToHashSet();
            var now = DateTime.Now;
            var done = folders.Where(Directory.Exists).SelectMany(f => new DirectoryInfo(f).EnumerateFiles("*.csv"))
                .Where(f => !f.Name.EndsWith("_summary.csv") && !seen.Contains(f.FullName) && now - f.LastWriteTime > Settled)
                .Select(f => (f, n: FromName(f.Name))).Where(x => x.n != null)
                .OrderBy(x => x.n!.Value.Start).ToList();
            bool changed = false;
            foreach (var group in done.GroupBy(x => x.n!.Value.Game))
            {
                // junta logs vizinhos: o próximo começou até 3 min depois do fim do anterior
                var batches = new List<List<FileInfo>>();
                foreach (var (f, _) in group)
                {
                    var last = batches.LastOrDefault();
                    if (last != null && FromName(f.Name)!.Value.Start - last[^1].LastWriteTime < Gap) last.Add(f);
                    else batches.Add(new() { f });
                }
                foreach (var b in batches)
                {
                    var rows = b.SelectMany(f => { try { return Parse(File.ReadLines(f.FullName)); } catch (IOException) { return new List<Row>(); } }).ToList();
                    var start = FromName(b[0].Name)!.Value.Start;
                    double minutes = (b[^1].LastWriteTime - start).TotalMinutes;
                    string game = group.Key;
                    double limit = AppSettings.Current.FpsLimits.TryGetValue("wine-" + game, out var l) ? l
                                 : AppSettings.Current.FpsLimits.GetValueOrDefault(game); // shortcut: o limite de agora, não o da hora da sessão
                    if (Summarize(game, start, rows, minutes, limit, b.Select(f => f.FullName).ToList()) is { } s) store.Sessions.Add(s);
                    store.Seen.AddRange(b.Select(f => f.FullName));
                    changed = true;
                }
            }
            if (Cleanup(store, ownFolder)) changed = true;
            if (!changed) return;
            store.Sessions = store.Sessions.OrderBy(s => s.Start).TakeLast(MaxSessions).ToList();
            store.Seen = store.Seen.TakeLast(MaxSeen).ToList();
            Save(store);
        }
    }

    /// <summary>Apaga da pasta do PHAROS os CSVs já resumidos com mais de 14 dias (e o _summary.csv que o MangoHud grava junto).</summary>
    private static bool Cleanup(Store store, string ownFolder)
    {
        bool any = false;
        if (!Directory.Exists(ownFolder)) return false;
        var seen = store.Seen.ToHashSet();
        foreach (var f in new DirectoryInfo(ownFolder).EnumerateFiles("*.csv"))
        {
            string main = f.Name.EndsWith("_summary.csv") ? Path.Combine(f.DirectoryName!, f.Name[..^12] + ".csv") : f.FullName;
            if (DateTime.Now - f.LastWriteTime < KeepLogs || !(seen.Contains(main) || !File.Exists(main))) continue;
            try { f.Delete(); any = true; } catch (IOException) { }
        }
        return any;
    }
}
