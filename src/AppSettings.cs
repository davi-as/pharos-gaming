using System.Text.Json;

namespace PharosGaming;

public sealed record BenchRun(DateTime Date, double? Multi, double? Single, bool Sustained, string Cpu);

/// <summary>Resultado dos benchmarks (Test = "gpu", "rt:preset:altura", "sim", "mem", "disk", "oc"). Score é usado para a barra do histórico.</summary>
public sealed record BenchRecord(DateTime Date, string Test, double Score, string Summary);

/// <summary>Programa extra que abre antes do jogo (ex.: ferramenta de comando/monitoramento) e pode fechar junto com ele.</summary>
public sealed class HelperProgram
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool CloseWithGame { get; set; } = true;
    public string Display => System.IO.Path.GetFileName(Path) + (string.IsNullOrWhiteSpace(Args) ? "" : " " + Args);
}

/// <summary>Perfil de um jogo na aba JOGOS: desempenho, argumentos de gráficos e programas auxiliares.</summary>
public sealed class GameProfile
{
    public bool MangoHud { get; set; } = true;         // grava o log que o painel lê
    public bool GameMode { get; set; }                 // gamemoderun
    public string WinMode { get; set; } = "default";   // default | full | borderless | window
    public string Res { get; set; } = "default";       // default | 1280x720 | 1920x1080 | 2560x1440 | 3840x2160
    public int Hz { get; set; }                        // 0 = não definir
    public string Style { get; set; } = "source";      // source | unity | unreal
    public string Extra { get; set; } = "";
    public List<HelperProgram> Helpers { get; set; } = new();
}

/// <summary>Jogo adicionado à mão (qualquer executável ou script).</summary>
public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
}

/// <summary>Configurações em ~/.config/pharos-gaming/settings.json.</summary>
public sealed class AppSettings
{
    // PHAROS_CONFIG_DIR: os testes usam uma pasta temporária para não mexer nas configurações de verdade
    public static string Folder { get; } = Environment.GetEnvironmentVariable("PHAROS_CONFIG_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pharos-gaming");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Instância única, compartilhada pelas páginas (cada uma grava a sua parte).</summary>
    public static AppSettings Current { get; } = Load();

    // PHAROS BENCH: últimos resultados (mais recente primeiro)
    public List<BenchRun> BenchRuns { get; set; } = new();
    public List<BenchRecord> BenchRecords { get; set; } = new();

    // Otimização: valores originais dos ajustes antes da primeira alteração (null = não existia)
    public Dictionary<string, string?> TweakBackup { get; set; } = new();

    // Inicialização: removidos (para restaurar) e serviços desligados pelo PHAROS (para poder religar)
    public List<StartupBackup> StartupRemoved { get; set; } = new();
    public List<string> StartupDisabledServices { get; set; } = new();

    // FPS estimado: a chave fica neste arquivo, que só o usuário lê (permissão 600)
    public string? HowManyFpsKey { get; set; }

    // Aparência: "system" (segue o tema do sistema), "light" (papiro) ou "dark" (noite do Nilo)
    public string Theme { get; set; } = "system";

    // Overlay: ligado, o PHAROS grava o MangoHud.conf (o do usuário fica guardado e volta ao desligar).
    // Nome novo de propósito: o antigo OverlayEnabled só valia para jogos abertos pelo PHAROS e não deve ligar isto sozinho.
    public bool OverlayManaged { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public string OverlayCorner { get; set; } = "TL";
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Itens escolhidos para o overlay (chaves de OverlayMetrics); null = padrão.</summary>
    public List<string>? OverlayItems { get; set; }

    // Limitador de FPS: limite por jogo (chave = nome do arquivo de config por jogo do MangoHud; 0 = sem limite)
    public Dictionary<string, double> FpsLimits { get; set; } = new();

    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
