namespace PharosGaming.Tests;

public class OptimizeTests
{
    [Fact]
    public void SysctlFile_AddsReplacesAndRemovesKeys()
    {
        string a = LinuxTweaks.SetLine("", "vm.max_map_count", "2147483642");
        Assert.StartsWith("# Gerado pelo PHAROS", a);
        Assert.Contains("vm.max_map_count=2147483642\n", a);
        string b = LinuxTweaks.SetLine(a, "kernel.split_lock_mitigate", "0");
        string c = LinuxTweaks.SetLine(b, "vm.max_map_count", null);
        Assert.DoesNotContain("max_map_count", c);
        Assert.Contains("kernel.split_lock_mitigate=0", c);
        Assert.Single(LinuxTweaks.SetLine(b, "kernel.split_lock_mitigate", "0").Split('\n'), l => l.Contains("split_lock"));
    }

    [Fact]
    public void SysctlScript_DeletesFileWhenEmptyAndQuotes()
    {
        string onlyComment = LinuxTweaks.SetLine("", "vm.swappiness", null);
        Assert.StartsWith("rm -f '/etc/sysctl.d/99-pharos-gaming.conf'", LinuxTweaks.Script(onlyComment, "vm.swappiness", "60"));
        string s = LinuxTweaks.Script(LinuxTweaks.SetLine("", "vm.swappiness", "10"), "vm.swappiness", "10");
        Assert.Contains("<<'PHAROS_EOF'", s);
        Assert.Contains("sysctl -w 'vm.swappiness=10'", s);
    }

    [Fact]
    public void Tweaks_SwappinessBlockedOnZram()
    {
        var t = LinuxTweaks.All().First(x => x.Id == "swappiness");
        bool zram = (Sys.Read("/proc/swaps") ?? "").Contains("/dev/zram");
        Assert.Equal(zram, !t.Available);
    }

    [Fact]
    public void ListApps_NeverIncludesSessionProcesses()
    {
        var apps = Optimizer.ListApps();
        Assert.DoesNotContain(apps, a => a.ProcessName is "Hyprland" or "pipewire" or "systemd" or "dotnet");
    }
}
