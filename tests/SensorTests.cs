namespace PharosGaming.Tests;

public class SensorTests
{
    private static string FakeHwmon()
    {
        string root = Directory.CreateTempSubdirectory("hwmon").FullName;
        void W(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!); File.WriteAllText(Path.Combine(root, path), text); }
        W("hwmon0/name", "k10temp\n");
        W("hwmon0/temp1_input", "61250\n");
        W("hwmon0/temp1_label", "Tctl\n");
        W("hwmon0/temp3_input", "-273000\n");   // sensor desligado: fica de fora
        W("hwmon1/name", "nct6687\n");
        W("hwmon1/fan1_input", "1200\n");
        W("hwmon1/in0_input", "1212\n");
        W("hwmon1/pwm1", "255\n");
        W("hwmon2/name", "nvme\n");
        W("hwmon2/device/model", "WDC PC SN530\n");
        W("hwmon2/temp1_input", "40850\n");
        W("hwmon2/power1_average", "15500000\n");
        return root;
    }

    [Fact]
    public void Hwmon_ConvertsUnitsAndNamesChips()
    {
        var r = SensorMonitor.ReadHwmon(FakeHwmon(), "AMD Ryzen 9 5900X").ToDictionary(x => x.Id);
        Assert.Equal(6, r.Count);
        Assert.Equal(61.25f, r["hwmon0/temp1_input"].Value);
        Assert.Equal("Tctl", r["hwmon0/temp1_input"].Name);
        Assert.Equal("AMD Ryzen 9 5900X", r["hwmon0/temp1_input"].Hardware);
        Assert.Equal("Placa-mãe (nct6687)", r["hwmon1/fan1_input"].Hardware);
        Assert.Equal(1.212f, r["hwmon1/in0_input"].Value!.Value, 3);
        Assert.Equal(100f, r["hwmon1/pwm1"].Value!.Value, 1);
        Assert.Equal("NVMe · WDC PC SN530", r["hwmon2/temp1_input"].Hardware);
        Assert.Equal(15.5f, r["hwmon2/power1_average"].Value!.Value, 2);
        Assert.Equal(SensorType.Power, r["hwmon2/power1_average"].Type);
    }

    [Fact]
    public void Row_TracksMinAndMax()
    {
        var row = new SensorRow { Type = SensorType.Temperature };
        row.Update(50); row.Update(70); row.Update(null); row.Update(60);
        Assert.Equal("60 °C", row.Value);
        Assert.Equal("50 °C", row.Min);
        Assert.Equal("70 °C", row.Max);
    }

    [Fact]
    public void Monitor_ReadsThisMachine()
    {
        var m = new SensorMonitor();
        m.Refresh();
        m.Refresh();
        Assert.Contains(m.Rows, r => r.Id == "cpu/load");
        Assert.Contains(m.Rows, r => r.Id == "mem/load");
    }
}
