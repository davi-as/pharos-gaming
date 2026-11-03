namespace PharosGaming.Tests;

public class ParsingTests
{
    [Fact]
    public void ProcStat_UsageFromTwoReadings()
    {
        var a = LinuxSensors.ParseProcStat("cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 50 0 50 400 0 0 0 0 0 0\nintr 1 2\n");
        var b = LinuxSensors.ParseProcStat("cpu  200 0 200 1400 200 0 0 0 0 0\ncpu0 150 0 50 400 0 0 0 0 0 0\n");
        Assert.Equal(2, a.Length);
        Assert.Equal(20f, LinuxSensors.Usage(a[0], b[0]), 1); // 200 de 1000 jiffies ocupados (iowait conta como ocioso)
        Assert.Equal(100f, LinuxSensors.Usage(a[1], b[1]), 1);
    }

    [Fact]
    public void Meminfo_ParsesKb()
    {
        var m = LinuxSensors.ParseMeminfo("MemTotal:       32767952 kB\nMemAvailable:   20000000 kB\nHugePages_Total:       0\n");
        Assert.Equal(32767952, m["MemTotal"]);
        Assert.Equal(0, m["HugePages_Total"]);
    }

    [Fact]
    public void MangoHud_FeedsRowsAndKeepsPartialLine()
    {
        var f = new MangoHudFrames();
        var now = DateTime.Now;
        f.Feed("os,cpu,gpu\nArch,Ryzen,RTX\nfps,frametime,cpu_load\n120.5,8.3,10\n60,16.6", now);
        Assert.Equal(120.5, f.Stats("x")!.Fps);
        f.Feed(",12\n", now);
        var s = f.Stats("x")!;
        Assert.Equal(60, s.Fps);
        Assert.Equal(16.6, s.FrameTimeMs);
        Assert.Equal(60, s.MinFps);
    }

    [Theory]
    [InlineData("glxgears_2026-10-22_21-30-52.csv", "glxgears")]
    [InlineData("Cyberpunk2077.exe_2026-01-02_03-04-05.csv", "Cyberpunk2077")]
    public void MangoHud_GameNameFromFile(string file, string game) => Assert.Equal(game, MangoHudFrames.GameName(file));

    [Fact]
    public void Classify_GpuBeforeCpu()
    {
        var s = new LoadSample(95, 40, 90, "thread 3", 50, 50, 70, 70, false, 100);
        Assert.Equal(Limiter.Gpu, s.Classify());
        Assert.Equal(Limiter.Cpu, (s with { GpuLoad = 60 }).Classify());
        Assert.Equal(Limiter.Vram, (s with { VramPct = 98 }).Classify());
    }

    [Fact]
    public void Classify_MainThreadAndThermal()
    {
        // o Elden Ring medido: GPU a 61% e quente (segurando o clock), CPU total 17%, thread principal a 87% de um núcleo
        var s = new LoadSample(61, 17, 40, null, 47, 30, 88, 68, true, 33, GameThread: 87);
        Assert.Equal(Limiter.MainThread, s.Classify());              // GPU quente mas sobrando não é o limite
        Assert.Equal(Limiter.Capped, (s with { GameThread = 40 }).Classify());
        Assert.Equal(Limiter.Thermal, (s with { GpuLoad = 97 }).Classify()); // cheia e segurando o clock: aí é térmico
        Assert.Equal(Limiter.Gpu, (s with { GpuLoad = 97, GpuThermalCap = false }).Classify());
    }

    [Fact]
    public void ThreadLoad_TicksFromStat()
    {
        // nome com espaço e parênteses (threads do Wine): os campos contam depois do último ")"
        const string stat = "2616618 (eldenring (x).exe) R 1 2 3 4 5 6 7 8 9 10 2900 1500 0 0 20 0 80";
        Assert.Equal(4400, ThreadLoad.Ticks(stat));
        Assert.Null(ThreadLoad.Ticks("1 (x) R 1"));
    }
}
