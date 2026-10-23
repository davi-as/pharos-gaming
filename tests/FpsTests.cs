namespace PharosGaming.Tests;

public class FpsTests
{
    [Fact]
    public void Catalog_KnowsThisHardwareByLinuxNames()
    {
        Assert.NotNull(GameCatalog.GpuPerf("NVIDIA GeForce RTX 3060"));          // nome que a NVML devolve
        Assert.NotNull(GameCatalog.CpuPerf("AMD Ryzen 9 5900X 12-Core Processor")); // nome do /proc/cpuinfo
    }

    [Fact]
    public void Estimate_DropsWithResolutionAndRespectsEngineCap()
    {
        var cs2 = GameCatalog.Games.First(g => g.Name == "Counter-Strike 2");
        var e1080 = GameCatalog.Estimate(cs2, 60, 80, "1920x1080", "Alto", "Native");
        var e4k = GameCatalog.Estimate(cs2, 60, 80, "3840x2160", "Alto", "Native");
        Assert.True(e4k.Avg < e1080.Avg);
        var capped = GameCatalog.Games.First(g => g.EngineCap > 0);
        Assert.True(GameCatalog.Estimate(capped, 400, 400, "1920x1080", "Baixo", "Performance").Avg <= capped.EngineCap);
    }

    [Fact]
    public void Row_RatingBands()
    {
        var r = new GameRow(GameCatalog.Games[0]);
        r.Set(150, 0, "ESTIMATIVA", "");
        Assert.Equal("EXCELENTE", r.Rating);
        r.Set(45, 0, "ESTIMATIVA", "");
        Assert.Equal("FRACO", r.Rating);
    }
}
