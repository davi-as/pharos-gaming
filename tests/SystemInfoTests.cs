namespace PharosGaming.Tests;

public class SystemInfoTests
{
    private const string Udev = """
        MEMORY_ARRAY_NUM_DEVICES=4
        MEMORY_ARRAY_MAX_CAPACITY=137438953472
        MEMORY_DEVICE_0_SIZE=8589934592
        MEMORY_DEVICE_0_LOCATOR=DIMM 0
        MEMORY_DEVICE_0_BANK_LOCATOR=P0 CHANNEL A
        MEMORY_DEVICE_0_TYPE=DDR4
        MEMORY_DEVICE_0_SPEED_MTS=3200
        MEMORY_DEVICE_0_CONFIGURED_SPEED_MTS=2133
        MEMORY_DEVICE_0_PART_NUMBER=KF3200C16D4/8GX
        MEMORY_DEVICE_0_DATA_WIDTH=64
        MEMORY_DEVICE_0_TOTAL_WIDTH=64
        MEMORY_DEVICE_1_SIZE=0
        MEMORY_DEVICE_1_LOCATOR=DIMM 1
        MEMORY_DEVICE_2_SIZE=8589934592
        MEMORY_DEVICE_2_LOCATOR=DIMM 0
        MEMORY_DEVICE_2_BANK_LOCATOR=P0 CHANNEL B
        MEMORY_DEVICE_2_CONFIGURED_SPEED_MTS=2133
        MEMORY_DEVICE_2_PART_NUMBER=KF3200C16D4/8GX
        """;

    [Fact]
    public void UdevMemory_SkipsEmptySlotsAndEstimatesProfile()
    {
        var (mods, slots, max) = SystemInfo.ParseUdevMemory(Udev);
        Assert.Equal(2, mods.Count);
        Assert.Equal(4, slots);
        Assert.Equal(128L << 30, max);
        Assert.StartsWith("Provavelmente DESLIGADO", SystemInfo.ProfileEstimate(mods)); // 2133 rodando, part number diz 3200
        Assert.StartsWith("2 canais", SystemInfo.ChannelEstimate(mods));
        Assert.StartsWith("Canal único: os módulos", SystemInfo.ChannelEstimate(new[] { mods[0], mods[0] }));
        Assert.StartsWith("Provavelmente ATIVO", SystemInfo.ProfileEstimate(new[] { mods[0] with { Configured = 3200 } }));
    }

    [Theory]
    [InlineData("P0 CHANNEL A", "A")]
    [InlineData("DIMM_B2", "B")]
    [InlineData("ChannelB-DIMM0", "B")]
    [InlineData("BANK 0", null)]
    public void ChannelFromLocator(string loc, string? expected) => Assert.Equal(expected, SystemInfo.ChannelOf(loc));

    [Fact]
    public void Lspci_KeepsOnlyDisplayControllers()
    {
        var g = SystemInfo.ParseLspci("""
            0000:2b:00.0 "VGA compatible controller [0300]" "NVIDIA Corporation [10de]" "GA106 [GeForce RTX 3060 Lite Hash Rate] [2504]" -ra1 -p00 "NVIDIA Corporation [10de]" "Device [1536]"
            0000:2b:00.1 "Audio device [0403]" "NVIDIA Corporation [10de]" "GA106 High Definition Audio Controller [228e]" -ra1 "NVIDIA Corporation [10de]" "Device [1536]"
            0000:0c:00.0 "Display controller [0380]" "Advanced Micro Devices, Inc. [AMD/ATI] [1002]" "Raphael [164e]" -rc1 "Device [1458]" "Device [d000]"
            """);
        Assert.Equal(2, g.Count);
        Assert.Equal("0000:2b:00.0", g[0].Slot);
        Assert.Equal("10de", g[0].VendorId);
        Assert.Equal("NVIDIA GeForce RTX 3060 Lite Hash Rate", SystemInfo.MarketingName(g[0]));
        Assert.Equal("AMD Raphael", SystemInfo.MarketingName(g[1]));
    }

    [Fact]
    public void CpuInfo_CountsCoresAndThreads()
    {
        string block(int p, int core) => $"processor\t: {p}\nvendor_id\t: AuthenticAMD\nmodel name\t: AMD Ryzen 9 5900X 12-Core Processor\nphysical id\t: 0\ncore id\t\t: {core}\nflags\t\t: fpu svm avx2 aes\n";
        var c = SystemInfo.ParseCpuInfo(string.Join("\n", Enumerable.Range(0, 4).Select(i => block(i, i % 2))));
        Assert.Equal(4, c.Threads);
        Assert.Equal(2, c.Cores);
        Assert.Contains("svm", c.Flags);
        Assert.Equal("AMD Ryzen 9 5900X 12-Core Processor", c.Model);
    }

    private const string Lsblk = """
        {"blockdevices":[
          {"name":"sda","model":"HS-SSD-C100 960G","tran":"sata","size":960197124096,"rota":false,"rm":false,"hotplug":false,"type":"disk","mountpoints":[null],
           "children":[
             {"name":"sda1","size":2147483648,"fstype":"vfat","mountpoints":["/boot"],"type":"part","rota":false,"rm":false},
             {"name":"sda2","size":958047191040,"fstype":"crypto_LUKS","mountpoints":[null],"type":"part","rota":"0","rm":"0",
              "children":[{"name":"root","size":958030413824,"fstype":"btrfs","mountpoints":["/var/log","/home","/"],"type":"crypt"}]}]},
          {"name":"sdb","model":"Pendrive","tran":"usb","size":16000000000,"rota":false,"rm":true,"type":"disk","mountpoints":["/run/media/x/PEN"],"label":"PEN","fstype":"exfat"},
          {"name":"zram0","size":4294967296,"type":"disk","mountpoints":["[SWAP]"]}
        ]}
        """;

    [Fact]
    public void Lsblk_TreeAndMountedVolumes()
    {
        var devs = SystemInfo.ParseLsblk(Lsblk);
        Assert.Equal(3, devs.Count);
        Assert.Equal("SSD", SystemInfo.Kind(devs[0]));
        Assert.Equal("USB", SystemInfo.Kind(devs[1]));
        Assert.True(devs[1].Removable);
        var drives = DriveMap.FromDevices(devs, mp => (100, 25));
        Assert.Equal(new[] { "/", "/boot", "/run/media/x/PEN" }, drives.Select(d => d.Mount)); // btrfs aparece uma vez só, pelo "/"
        Assert.Equal("HS-SSD-C100 960G", drives[0].Disk);
        Assert.Equal(0.75, drives[0].UsedFraction);
        Assert.True(drives[2].Removable);
    }

    [Fact]
    public void Collect_ReadsThisMachineWithoutThrowing()
    {
        var roots = SystemInfo.Collect();
        Assert.Equal(6, roots.Count);
        Assert.All(roots, r => Assert.NotEmpty(r.Children));
    }
}
