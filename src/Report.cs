namespace PharosGaming;

/// <summary>Junta o que cada página sabe medir para o relatório exportado (a leitura dos discos e da energia é refeita na hora).</summary>
public static class Report
{
    public static async Task<ReportData> CollectAsync(IReadOnlyList<InfoNode> system)
    {
        var info = system.Count > 0 ? system : await Task.Run(SystemInfo.Collect);
        var data = new ReportData
        {
            Date = DateTime.Now,
            Machine = Environment.MachineName,
            AppVersion = typeof(Report).Assembly.GetName().Version?.ToString(3) ?? "",
            System = info.Select(ReportBuilder.ToNode).OfType<ReportNode>().ToList(),
        };
        var smart = DisksPage.LastSmart;
        data.Disks.AddRange((await Task.Run(() => DiskHealth.Read(smart))).Select(ReportDisk.From));
        var rows = SensorMonitor.Shared.Rows.ToList();
        data.Power.AddRange((await Task.Run(() => BatteryInfo.Read(rows))).Select(ReportDisk.From));
        if (NetworkTest.Last != null) data.Network.AddRange(NetworkTest.Last.Select(ReportDisk.From));
        var gpus = await Task.Run(GpuInfoCollector.Collect);
        data.Gpus.AddRange(gpus.Select(g => new ReportGpu(g.Name, g.Subtitle, g.Sections.Select(ReportBuilder.ToNode).OfType<ReportNode>().ToList())));
        var sensors = SensorMonitor.Shared;
        sensors.Refresh();
        data.Sensors.AddRange(sensors.Rows.Select(s => new ReportSensor(s.Hardware, s.Category, s.Name, s.Value, s.Min, s.Max)));
        var set = AppSettings.Current;
        data.Benchmarks.AddRange(set.BenchRecords.Select(r => new ReportBench(r.Date, BenchLog.TestName(r.Test), r.Summary)));
        foreach (var r in set.BenchRuns)
        {
            var parts = new List<string>();
            if (r.Multi is double mt) parts.Add($"multi-core {mt:N0} pts");
            if (r.Single is double st) parts.Add($"single-core {st:N0} pts");
            if (r.Sustained) parts.Add("sustentado");
            data.Benchmarks.Add(new ReportBench(r.Date, "Render (CPU)", string.Join(" · ", parts) + (r.Cpu.Length > 0 ? $" · {r.Cpu}" : "")));
        }
        data.Benchmarks.Sort((a, b) => b.Date.CompareTo(a.Date));
        return data;
    }
}
