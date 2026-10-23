namespace PharosGaming;

/// <summary>Medidor com rótulo, valor formatado e fração (0..1) para a barra.</summary>
public sealed record MeterItem(string Label, string Value, double Fraction)
{
    public static MeterItem Pct(string label, double? pct) =>
        new(label, pct == null ? "—" : $"{pct:0} %", Math.Clamp((pct ?? 0) / 100, 0, 1));
}

/// <summary>Linha de resultado: nome, valor e uma dica opcional embaixo.</summary>
public sealed record Kv(string Key, string Value, string Hint = "")
{
    public bool HasHint => Hint.Length > 0;
}
