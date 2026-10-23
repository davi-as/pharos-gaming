using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PharosGaming;

public sealed record FrameStats(string Game, double Fps, double FrameTimeMs, double MinFps);

/// <summary>
/// FPS do jogo pelo log CSV do MangoHud, que ele grava (e descarrega) linha a linha enquanto o jogo roda.
/// No Linux não há um ETW/PresentMon do sistema: a camada Vulkan/OpenGL do MangoHud é quem vê cada frame.
/// Lê o CSV mais recente da pasta do PHAROS e da output_folder configurada no MangoHud.conf do usuário.
/// </summary>
public sealed partial class MangoHudFrames
{
    public static readonly string OwnFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pharos-gaming", "mangohud");

    // read_cfg primeiro: sem ele o MangoHud ignora o MangoHud.conf do usuário quando MANGOHUD_CONFIG está definido
    public static string LogConfig => $"read_cfg,autostart_log=1,log_interval=250,output_folder={OwnFolder}";

    private const double WindowSec = 10; // janela do mínimo
    private readonly string[] _folders;
    private string? _file;
    private long _offset;
    private readonly StringBuilder _partial = new();
    private int _colFps = -1, _colFt = -1;
    private readonly Queue<(DateTime At, double Fps, double Ft)> _hist = new();
    private readonly List<double> _all = new(); // a sessão inteira deste log (tempo abaixo de 60)
    private readonly Queue<double> _recent = new(); // os últimos tempos de quadro, para o gráfico do menu
    private const int RecentMax = 240;

    /// <summary>Os últimos 240 tempos de quadro (60 s com log a cada 250 ms).</summary>
    public IReadOnlyList<double> RecentFrameTimes => _recent.ToList();

    /// <summary>"Nunca 59" desde o começo deste log.</summary>
    public Floor SessionFloor => Floor.Of(_all);

    /// <summary>Quando o log atual começou (pelo nome do arquivo).</summary>
    public DateTime? SessionStart => _file != null ? SessionLog.FromName(_file)?.Start : null;

    public MangoHudFrames()
    {
        Directory.CreateDirectory(OwnFolder);
        _folders = Folders();
    }

    /// <summary>Onde os logs aparecem: a pasta do PHAROS e a output_folder do usuário (ou a pasta pessoal, o padrão do MangoHud).</summary>
    public static string[] Folders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { OwnFolder, UserOutputFolder(home) ?? home }.Distinct().ToArray();
    }

    /// <summary>output_folder do ~/.config/MangoHud/MangoHud.conf (o MangoHud usa a pasta pessoal se não houver).</summary>
    private static string? UserOutputFolder(string home)
    {
        try
        {
            var line = File.ReadLines(Path.Combine(home, ".config/MangoHud/MangoHud.conf"))
                           .Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("output_folder="));
            return line?.Split('=', 2)[1].Trim().Replace("~", home);
        }
        catch (IOException) { return null; }
    }

    /// <summary>Lê o que o MangoHud gravou desde a última chamada. Null se nenhum jogo está gravando agora.</summary>
    public FrameStats? Poll()
    {
        var newest = _folders.Where(Directory.Exists)
            .SelectMany(f => new DirectoryInfo(f).EnumerateFiles("*.csv"))
            .Where(f => !f.Name.EndsWith("_summary.csv"))
            .MaxBy(f => f.LastWriteTimeUtc);
        if (newest == null || DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromSeconds(3)) return null;

        if (newest.FullName != _file)
        {
            _file = newest.FullName;
            _offset = 0;
            _partial.Clear();
            _hist.Clear();
            _all.Clear();
            _recent.Clear();
            _colFps = _colFt = -1;
        }

        try
        {
            using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            Feed(sr.ReadToEnd(), DateTime.Now);
            _offset = fs.Position;
        }
        catch (IOException) { return null; }

        return Stats(GameName(newest.Name));
    }

    /// <summary>Processa um pedaço do CSV; a última linha pode chegar incompleta e fica guardada para a próxima leitura.</summary>
    internal void Feed(string chunk, DateTime now)
    {
        _partial.Append(chunk);
        string all = _partial.ToString();
        int cut = all.LastIndexOf('\n');
        if (cut < 0) return;
        _partial.Clear().Append(all[(cut + 1)..]);

        foreach (var line in all[..cut].Split('\n'))
        {
            var cols = line.Trim().Split(',');
            if (cols[0] == "fps")
            {
                _colFps = 0;
                _colFt = Array.IndexOf(cols, "frametime");
                continue;
            }
            if (_colFps < 0 || !double.TryParse(cols[_colFps], NumberStyles.Float, CultureInfo.InvariantCulture, out double fps)) continue;
            double ft = _colFt >= 0 && _colFt < cols.Length &&
                        double.TryParse(cols[_colFt], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 1000 / fps;
            _hist.Enqueue((now, fps, ft));
            _all.Add(fps);
            _recent.Enqueue(ft);
            if (_recent.Count > RecentMax) _recent.Dequeue();
        }
        while (_hist.Count > 0 && (now - _hist.Peek().At).TotalSeconds > WindowSec) _hist.Dequeue();
    }

    /// <summary>Tempos de quadro dos últimos 10 s (para o veredito do limitador).</summary>
    public IReadOnlyList<double> FrameTimes => _hist.Select(h => h.Ft).ToList();

    internal FrameStats? Stats(string game)
    {
        if (_hist.Count == 0) return null;
        var last = _hist.Last();
        // shortcut: o CSV traz a média de cada intervalo, não cada frame; mínimo de 10 s em vez de 1% low real
        return new FrameStats(game, last.Fps, last.Ft, _hist.Min(h => h.Fps));
    }

    /// <summary>"Cyberpunk2077.exe_2026-10-22_21-30-52.csv" → "Cyberpunk2077".</summary>
    internal static string GameName(string file)
    {
        string n = StampRe().Replace(file, "");
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    [GeneratedRegex(@"_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.csv$")]
    private static partial Regex StampRe();
}
