namespace PharosGaming;

/// <summary>Medidor com rótulo, valor formatado e fração (0..1) para a barra.</summary>
public sealed record MeterItem(string Label, string Value, double Fraction)
{
    public static MeterItem Pct(string label, double? pct) =>
        new(label, pct == null ? "—" : $"{pct:0} %", Math.Clamp((pct ?? 0) / 100, 0, 1));
}
