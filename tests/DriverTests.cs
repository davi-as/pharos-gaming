namespace PharosGaming.Tests;

public class DriverTests
{
    [Fact]
    public void ParseUpdates_PacmanAptDnf()
    {
        var pac = DriverManager.ParseUpdates("nvidia-utils 615.78.08-1 -> 620.10-1\nc-ares 1.34.8-1 -> 1.34.9-1\n");
        Assert.Equal("nvidia-utils", pac[0].Name);           // driver primeiro
        Assert.True(pac[0].DriverRelated);
        Assert.False(pac[1].DriverRelated);
        var apt = DriverManager.ParseUpdates("Listing... Done\nmesa-vulkan-drivers/noble-updates 24.2.8 amd64 [upgradable from: 24.2.1]\n");
        Assert.Equal(("mesa-vulkan-drivers", "24.2.1", "24.2.8"), (apt[0].Name, apt[0].From, apt[0].To));
        var dnf = DriverManager.ParseUpdates("kernel.x86_64  6.11.4-301.fc41  updates\n");
        Assert.Equal("kernel", dnf[0].Name);
    }

    [Theory]
    [InlineData("620.10", "615.78.08", 1)]
    [InlineData("615.78.08", "615.78.08", 0)]
    [InlineData("580.95.05", "615.78", -1)]
    public void CompareVersions(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(GpuDriverStatus.CompareVersions(a, b)));

    [Fact]
    public void NvidiaAdvice_PrefersDistroOverRunInstaller()
    {
        var g = new GpuDriverStatus { Installed = "615.78.08", Repo = "615.78.08", Latest = "620.10" };
        Assert.Contains("evite o instalador .run", DriverManager.NvidiaAdvice(g));
        g.Repo = "620.10";
        Assert.Contains("ATUALIZAR O SISTEMA", DriverManager.NvidiaAdvice(g));
        g.Installed = "620.10";
        Assert.Contains("mais recente", DriverManager.NvidiaAdvice(g));
    }

    [Fact]
    public void Inventory_ThisMachine() => Assert.NotEmpty(DriverManager.Inventory());
}
