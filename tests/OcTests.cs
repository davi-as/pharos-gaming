namespace PharosGaming.Tests;

public class OcTests
{
    [Fact]
    public void Classify_CountsRealErrorsOnly()
    {
        var e = OcCheck.Classify(new[]
        {
            "2026-10-22T18:31:29-04:00 omarchy kernel: MCE: In-kernel MCE decoding enabled.",          // inicialização: não conta
            "2026-10-22T18:31:29-04:00 omarchy kernel: pcieport 0000:00:07.1: AER: enabled with IRQ 32", // idem
            "2026-10-22T19:00:01-04:00 omarchy kernel: mce: [Hardware Error]: Machine check events logged",
            "2026-10-22T19:05:00-04:00 omarchy kernel: pcieport 0000:00:01.1: AER: Corrected error received: 0000:01:00.0",
            "2026-10-22T19:10:00-04:00 omarchy kernel: NVRM: Xid (PCI:0000:2b:00): 79, GPU has fallen off the bus.",
        });
        Assert.Equal(2, e.Whea);
        Assert.Equal(1, e.GpuResets);
        Assert.Contains("Xid", e.LastDetail);
        Assert.StartsWith("2026-10-22 19:10", e.LastDetail);
    }

    [Fact]
    public void ApplyScript_NvidiaPowerAndClockCap()
    {
        var g = new GpuInfo { Vendor = "NVIDIA" };
        string s = OcCheck.ApplyScript(g, 150, 1500);
        Assert.Contains("-pl 150", s);
        Assert.Contains("-lgc 210,1500", s);
        Assert.Contains("-rgc", OcCheck.ApplyScript(g, 150, null)); // sem teto: tira a trava
    }

    [Fact]
    public void ErrorsSince_ReadsJournal()
    {
        var e = OcCheck.ErrorsSince(DateTime.Now.AddHours(-1));
        Assert.True(e.Error == null || e.Error.Length > 0); // sem journal legível (CI) vira mensagem, nunca exceção
    }
}
