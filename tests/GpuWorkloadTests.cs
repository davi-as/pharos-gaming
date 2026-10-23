namespace PharosGaming.Tests;

/// <summary>
/// Roda cada carga de GPU por um instante quando há GPU com aceleração (EGL). Em máquinas sem GPU (CI) o teste
/// passa sem rodar: o que importa lá são os cálculos, cobertos pelos outros testes.
/// </summary>
public class GpuWorkloadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static bool HasGpu()
    {
        try { using var c = GlContext.Create(); return true; }
        catch { return false; }
    }

    [Fact]
    public void Scene_RendersFramesAndPreview()
    {
        if (!HasGpu()) { output.WriteLine("sem GPU com aceleração: pulado"); return; }
        var b = new GpuGameBench { Width = 640, Height = 360, Duration = TimeSpan.FromSeconds(1.5) };
        var r = b.Run();
        Assert.True(r != null, b.Error);
        Assert.True(r!.Frames > 10);
        Assert.True(r.AvgFps > 0 && r.Low1Fps > 0 && r.Low1Fps <= r.AvgFps * 1.01);
        Assert.True(b.Preview!.Version > 0);
        Assert.Contains(b.Preview.Pixels, px => px > 30); // a prévia não é uma tela preta
    }

    [Fact]
    public void RayTracing_MedianOfPasses()
    {
        if (!HasGpu()) { output.WriteLine("sem GPU com aceleração: pulado"); return; }
        var b = new GpuRtBench { Width = 320, Height = 180, Preset = "normal", Warmup = TimeSpan.FromSeconds(0.3), PassDuration = TimeSpan.FromSeconds(0.5), Passes = 3 };
        var r = b.Run();
        Assert.True(r != null, b.Error);
        Assert.Equal(3, r!.Passes.Count);
        Assert.True(r.MedianFps > 0 && r.GigaRaysPerSec > 0);
    }

    [Fact]
    public void Burn_RunsAndStops()
    {
        if (!HasGpu()) { output.WriteLine("sem GPU com aceleração: pulado"); return; }
        var b = new GpuBurn();
        b.Start(new GpuBurnSettings { Width = 320, Height = 180, Shells = 8, Iterations = 0 });
        Thread.Sleep(1500);
        Assert.True(b.Running, b.Error);
        b.Stop();
        Assert.False(b.Running);
        Assert.True(b.Frames > 0);
    }

    [Fact]
    public void Context_RefusesSoftwareRenderers()
    {
        Assert.True(GlContext.IsSoftware("llvmpipe (LLVM 23.1.3, 256 bits)"));
        Assert.False(GlContext.IsSoftware("NVIDIA GeForce RTX 3060/PCIe/SSE2"));
    }

    [Fact]
    public void FrameStats_AverageAndOnePercentLow()
    {
        var times = Enumerable.Repeat(10.0, 99).Append(50.0).ToList(); // 99 frames de 10 ms e uma travada de 50 ms
        var (avg, low1) = GpuGameBench.FrameStatsOf(times);
        Assert.Equal(100 / 1.04, avg, 1);
        Assert.Equal(20, low1, 1);
        Assert.Equal(new[] { 2.0 }, new[] { GpuRtResult.Median(new[] { 3.0, 1, 2 }) });
    }

    [Fact]
    public void RtGrid_EveryObjectInsideItsCells()
    {
        var objs = new List<(System.Numerics.Vector3, float)> { (new(0, 1, 0), 1), (new(-111, 1, 111), 2) };
        var (start, items) = GpuRtBench.BuildGrid(objs, 56, -112, 4);
        Assert.Equal(56 * 56 + 1, start.Length);
        int Cell(float x, float z) => (int)((z + 112) / 4) * 56 + (int)((x + 112) / 4);
        bool In(int cell, int obj) => items[start[cell]..start[cell + 1]].Contains(obj);
        Assert.True(In(Cell(0, 0), 0));
        Assert.True(In(Cell(-111, 111), 1));
        Assert.False(In(Cell(50, 50), 0));
    }
}
