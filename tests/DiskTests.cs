namespace PharosGaming.Tests;

public class DiskTests
{
    private const string Dump = """
        /org/freedesktop/UDisks2/block_devices/sda:
          org.freedesktop.UDisks2.Block:
            Drive:                      '/org/freedesktop/UDisks2/drives/HS_SSD'
        /org/freedesktop/UDisks2/drives/HS_SSD:
          org.freedesktop.UDisks2.Drive:
            Model:                      HS-SSD-C100 960G
            Serial:                     30113363467
          org.freedesktop.UDisks2.Drive.Ata:
            SmartFailing:               false
            SmartNumBadSectors:         3
            SmartPowerOnSeconds:        21628800
            SmartTemperature:           302.0
        /org/freedesktop/UDisks2/drives/WDC:
          org.freedesktop.UDisks2.Drive:
            Serial:                     23054D808599
          org.freedesktop.UDisks2.NVMe.Controller:
            SmartCriticalWarning:       spare
            SmartPowerOnHours:          4610
            SmartTemperature:           312
        """;

    [Fact]
    public void Udisks_DumpGivesSmartWithoutRoot()
    {
        var all = DiskHealth.ParseUdisksDump(Dump);
        Assert.Equal(2, all.Count);
        var sata = new DiskHealth.Raw { Media = "SSD" };
        DiskHealth.ApplyUdisks(sata, all["/org/freedesktop/UDisks2/drives/HS_SSD"]);
        Assert.Equal(28.85, sata.Temp!.Value, 2);
        Assert.Equal(6008, sata.PowerOnHours);
        Assert.False(sata.PredictFail);
        Assert.Equal(3, sata.BadSectors);
        Assert.Equal(Verdict.Warning, DiskHealth.ToCard(sata).Verdict); // setores ruins

        var nvme = new DiskHealth.Raw { Media = "SSD" };
        DiskHealth.ApplyUdisks(nvme, all["/org/freedesktop/UDisks2/drives/WDC"]);
        Assert.True(nvme.PredictFail); // aviso crítico "spare"
        Assert.Equal(Verdict.Critical, DiskHealth.ToCard(nvme).Verdict);
    }

    [Fact]
    public void Smartctl_NvmeAndAta()
    {
        var nv = new DiskHealth.Raw { Media = "SSD" };
        DiskHealth.ApplySmartctl(nv, """
            {"smart_status":{"passed":true},"temperature":{"current":39},"power_on_time":{"hours":4610},
             "nvme_smart_health_information_log":{"critical_warning":0,"percentage_used":3,"data_units_written":20000000,"media_errors":0,"unsafe_shutdowns":12}}
            """);
        Assert.Equal(3, nv.Wear);
        Assert.Equal(10240, nv.WrittenGb!.Value, 0);
        Assert.Equal(12, nv.UnsafeShutdowns);
        var card = DiskHealth.ToCard(nv);
        Assert.Equal(Verdict.Good, card.Verdict);
        Assert.Contains(card.Metrics, m => m.Key == "Vida útil restante" && m.Value == "97%");

        var ata = new DiskHealth.Raw { Media = "SSD" };
        DiskHealth.ApplySmartctl(ata, """
            {"smart_status":{"passed":false},"ata_smart_attributes":{"table":[
              {"id":5,"value":100,"raw":{"value":8}},{"id":177,"value":12,"raw":{"value":900}},{"id":241,"value":100,"raw":{"value":2000000000}}]}}
            """);
        Assert.Equal(8, ata.Reallocated);
        Assert.Equal(88, ata.Wear);
        Assert.Equal(1024, ata.WrittenGb!.Value, 0);
        Assert.Equal(Verdict.Critical, DiskHealth.ToCard(ata).Verdict); // S.M.A.R.T. reprovado
    }

    [Fact]
    public void Smartctl_ScriptQuotesAndSplits()
    {
        string script = DiskHealth.SmartctlScript(new[] { "/dev/sda", "/dev/x'y" });
        Assert.Contains("smartctl -j -a '/dev/sda'", script);
        Assert.Contains("'/dev/x'\\''y'", script); // aspas no nome não escapam do shell
        var parts = DiskHealth.SplitSmartctl("### /dev/sda\n{\"a\":1}\n### /dev/nvme0n1\n{\"b\":2}\n");
        Assert.Equal(2, parts.Count);
        Assert.Contains("\"b\"", parts["/dev/nvme0n1"]);
    }

    [Fact]
    public void Read_ThisMachine()
    {
        var cards = DiskHealth.Read();
        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.False(string.IsNullOrEmpty(c.Title)));
    }
}
