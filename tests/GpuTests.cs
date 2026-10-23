namespace PharosGaming.Tests;

public class GpuTests
{
    [Fact]
    public void LargestBar_DetectsResizableBar()
    {
        const string res = "0x00000000fb000000 0x00000000fbffffff 0x0000000000040200\n" +
                           "0x0000007800000000 0x0000007bffffffff 0x000000000014220c\n" +
                           "0x0000000000000000 0x0000000000000000 0x0000000000000000\n";
        Assert.Equal(16UL << 30, GpuInfoCollector.LargestBar(res));
        Assert.Equal(0UL, GpuInfoCollector.LargestBar(""));
    }

    [Theory]
    [InlineData("16.0 GT/s PCIe", 4u)]
    [InlineData("8.0 GT/s PCIe", 3u)]
    [InlineData("2.5 GT/s PCIe", 1u)]
    [InlineData("Unknown", null)]
    public void PcieGenFromSysfs(string speed, uint? gen) => Assert.Equal(gen, GpuInfo.PcieGen(speed));

    [Fact]
    public void Collect_ReadsThisMachine()
    {
        var gpus = GpuInfoCollector.Collect();
        Assert.All(gpus, g => Assert.DoesNotContain(g.Sections, s => s.Header == "ERRO"));
    }
}
