namespace PharosGaming.Tests;

public class EnergyTests
{
    private static string Fake(params (string Path, string Text)[] files)
    {
        string root = Directory.CreateTempSubdirectory("psu").FullName;
        foreach (var (p, t) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, p))!);
            File.WriteAllText(Path.Combine(root, p), t + "\n");
        }
        return root;
    }

    [Fact]
    public void Battery_WearCyclesAndRuntime()
    {
        string root = Fake(("AC/type", "Mains"), ("AC/online", "0"),
                           ("BAT0/type", "Battery"), ("BAT0/status", "Discharging"), ("BAT0/capacity", "50"),
                           ("BAT0/energy_full_design", "60000000"), ("BAT0/energy_full", "39000000"), ("BAT0/energy_now", "19500000"),
                           ("BAT0/power_now", "9750000"), ("BAT0/cycle_count", "850"), ("BAT0/technology", "Li-ion"),
                           ("hidpp_battery_0/type", "Battery"), ("hidpp_battery_0/scope", "Device")); // bateria do mouse não conta
        var cards = BatteryInfo.Read(root, "balanced", null);
        Assert.Equal(2, cards.Count);
        var bat = cards[0];
        Assert.Equal(Verdict.Warning, bat.Verdict);              // 35% de desgaste
        Assert.Contains("850 ciclos", bat.Reasons);
        Assert.Contains(bat.Metrics, m => m.Key == "Autonomia estimada" && m.Value == "2h 00min");
        Assert.Contains(bat.Metrics, m => m.Key == "Química" && m.Value == "Íon-lítio");
        Assert.Contains(cards[1].Metrics, m => m.Key == "Fonte de energia" && m.Value == "Bateria");
        Assert.Contains("perfil Desempenho", cards[1].Reasons);
    }

    [Fact]
    public void Desktop_OnlyEnergyCard()
    {
        var cards = BatteryInfo.Read(Fake(("AC/type", "Mains")), "performance", null);
        Assert.Single(cards);
        Assert.Contains(cards[0].Metrics, m => m.Key == "Fonte de energia" && m.Value == "Tomada (PC de mesa)");
        Assert.Contains(cards[0].Metrics, m => m.Key == "Perfil de energia" && m.Value == "Desempenho");
    }
}
