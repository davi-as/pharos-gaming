namespace PharosGaming.Tests;

public class OverlayTests
{
    [Fact]
    public void Build_MapsChoicesToMangoHudOptions()
    {
        var conf = MangoHudConfig.Build(new[] { "fps", "low1", "gpu_temp", "cpu_maxcore", "game" }, "BR", 1.3, visible: false).Split('\n');
        Assert.Contains("position=bottom-right", conf);
        Assert.Contains("font_size=31", conf);
        Assert.Contains("no_display", conf);
        Assert.Contains("fps_metrics=avg,0.01", conf);
        Assert.Contains("gpu_temp", conf);
        Assert.Contains("core_load", conf);
        Assert.Contains("exec_name", conf);
        Assert.DoesNotContain("gpu_stats", conf);
        Assert.Contains("toggle_hud=Control_L+Shift_L+Q", conf);
    }

    [Fact]
    public void EveryCatalogItemHasAnOption()
    {
        var all = MangoHudConfig.Build(OverlayMetrics.All.Select(m => m.Key), "TL", 1, true).Split('\n');
        var fixedLines = MangoHudConfig.Build(Array.Empty<string>(), "TL", 1, true).Split('\n');
        Assert.True(all.Length - fixedLines.Length >= OverlayMetrics.All.Length); // nenhum item fica sem opção do MangoHud
    }

    [Fact]
    public void LogConfig_KeepsUserConfigFile() =>
        Assert.StartsWith("read_cfg,", MangoHudFrames.LogConfig); // sem read_cfg o MangoHud ignora o MangoHud.conf
}
