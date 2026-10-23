namespace PharosGaming;

public enum Verdict { Good, Warning, Critical, Unknown }

/// <summary>
/// Cartão de resultado com os textos prontos para a tela e o relatório: discos, energia, rede, drivers...
/// (no PHAROS para Windows era o DiskHealthCard).
/// </summary>
public sealed class InfoCard
{
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public Verdict Verdict { get; init; } = Verdict.Unknown;
    /// <summary>Texto do selo; quando vazio, usa o padrão de saúde de disco.</summary>
    public string? Label { get; init; }
    public string VerdictText => Label ?? Verdict switch
    {
        Verdict.Good => "SAUDÁVEL",
        Verdict.Warning => "ATENÇÃO",
        Verdict.Critical => "CRÍTICO",
        _ => "SEM DADOS"
    };
    /// <summary>Atenção, crítico e "sem dados" ganham destaque (selo preenchido); saudável fica só com contorno.</summary>
    public bool IsAlert => Verdict != Verdict.Good;
    public string Reasons { get; init; } = "";
    public List<KeyValuePair<string, string>> Metrics { get; init; } = new();
}
