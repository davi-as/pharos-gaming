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
        return data;
    }
}
