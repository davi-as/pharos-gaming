using Avalonia.Headless.XUnit;

namespace PharosGaming.Tests;

public class SessionTests
{
    private static IEnumerable<string> Csv(int rows, double ft, double jitter = 0)
    {
        yield return "os,cpu,gpu,ram,kernel,driver,cpuscheduler";
        yield return "Arch,Ryzen,RTX,32,7.2,,performance";
        yield return "fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,cpu_mhz,elapsed";
        for (int i = 0; i < rows; i++)
        {
            double t = ft + (i % 2 == 0 ? jitter : -jitter);
            yield return FormattableString.Invariant($"{1000 / t},{t},20,0,{60 + i % 3},{70 + i % 5},55,1800,7000,3.5,90,11,0,0,4500,{i * 250_000_000L}");
        }
    }

    [Fact]
    public void Parse_And_Summarize()
    {
        var rows = SessionLog.Parse(Csv(400, 16.67));
        Assert.Equal(400, rows.Count);
        Assert.Equal(55, rows[0].GpuTemp);
        var s = SessionLog.Summarize("Hades", new DateTime(2026, 10, 23, 12, 0, 0), rows, 10, 60, new() { "a.csv" })!;
        Assert.Equal(60, s.AvgFps, 0);
        Assert.Equal(60, s.Low1Fps, 0);
        Assert.True(s.PacingMs < 0.01);
        Assert.Equal(74, s.CpuTempMax);
        Assert.Null(SessionLog.Summarize("x", DateTime.Now, rows.Take(10).ToList(), 10, 0, new())); // curta demais
        var shaky = SessionLog.Summarize("x", DateTime.Now, SessionLog.Parse(Csv(400, 16.67, 5)), 10, 0, new())!;
        Assert.True(shaky.PacingMs > 4);
        Assert.True(shaky.Low1Fps < 50);
    }

    [Fact]
    public void FromName_StripsExeAndReadsDate()
    {
        Assert.Equal(("eldenring", new DateTime(2026, 10, 23, 12, 17, 57)), SessionLog.FromName("/x/eldenring.exe_2026-10-23_12-17-57.csv"));
        Assert.Null(SessionLog.FromName("Hades_2026-10-23_12-17-57_summary.csv"));
    }

    [AvaloniaFact] // AppSettings.Folder temporário do TestApp
    public void Scan_MergesNeighbourLogsAndCleansOldOnes()
    {
        var dir = Directory.CreateTempSubdirectory("pharos-logs").FullName;
        var old = DateTime.Now.AddDays(-20);
        void Write(string name, DateTime end, int rows)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllLines(p, Csv(rows, 16.67));
            File.SetLastWriteTime(p, end);
        }
        // duas partes da mesma sessão (o limite mudou no meio) e uma sessão separada dias depois
        Write("Hades_" + old.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv", old.AddMinutes(5), 1200);
        Write("Hades_" + old.AddMinutes(6).ToString("yyyy-MM-dd_HH-mm-ss") + ".csv", old.AddMinutes(20), 3600);
        var later = DateTime.Now.AddHours(-2);
        Write("Hades_" + later.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv", later.AddMinutes(30), 7200);
        File.Delete(SessionLog.FilePath);

        SessionLog.Scan(new[] { dir }, dir);
        var st = SessionLog.Load();
        Assert.Equal(2, st.Sessions.Count);
        Assert.Equal(20, st.Sessions[0].Minutes, 0);
        Assert.Equal(2, st.Sessions[0].Files.Count);
        Assert.Single(Directory.GetFiles(dir)); // os dois logs de 20 dias atrás, já resumidos, foram apagados
        SessionLog.Scan(new[] { dir }, dir);
        Assert.Equal(2, SessionLog.Load().Sessions.Count); // não resume de novo
    }
}
