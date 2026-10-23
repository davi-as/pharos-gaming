namespace PharosGaming;

/// <summary>
/// Bateria (desgaste, ciclos, carga) e energia (fonte, perfil, consumo dos sensores), por /sys/class/power_supply e
/// power-profiles-daemon. Nada é alterado. Em PC de mesa não há bateria: aparece só o cartão de energia.
/// </summary>
public static class BatteryInfo
{
    public static List<InfoCard> Read(IEnumerable<SensorRow>? sensors = null) => Read("/sys/class/power_supply", PowerProfile(), sensors);

    public static string? PowerProfile()
    {
        if (!Sys.Has("powerprofilesctl")) return null;
        var r = Sys.RunAsync("powerprofilesctl", "get").GetAwaiter().GetResult();
        return r.Ok && r.Out.Trim().Length > 0 ? r.Out.Trim() : null;
    }

    public static List<InfoCard> Read(string root, string? profile, IEnumerable<SensorRow>? sensors)
    {
        var cards = new List<InfoCard>();
        var supplies = Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(d => d).ToList() : new List<string>();
        var bats = supplies.Where(d => Sys.Read(Path.Combine(d, "type")) == "Battery" && Sys.Read(Path.Combine(d, "scope")) != "Device").ToList();
        bool onAc = supplies.Any(d => Sys.Read(Path.Combine(d, "type")) == "Mains" && Sys.Read(Path.Combine(d, "online")) == "1");

        foreach (var b in bats)
        {
            string? S(string f) => Sys.Read(Path.Combine(b, f));
            long? L(string f) => Sys.ReadLong(Path.Combine(b, f));
            // energia em µWh; alguns notebooks só informam carga em µAh (charge_*) e a tensão
            long? design = L("energy_full_design") ?? L("charge_full_design");
            long? full = L("energy_full") ?? L("charge_full");
            bool energy = L("energy_full_design") != null;
            long? cycles = L("cycle_count");
            if (cycles == 0) cycles = null; // muitos fabricantes não informam (vem 0)
            long? pct = L("capacity");
            string status = S("status") ?? "";
            bool charging = status == "Charging", discharging = status == "Discharging";
            if (!discharging && status.Length > 0) onAc = true;

            double? wear = design is > 0 && full is > 0 ? Math.Max(0, 100.0 * (1 - (double)full.Value / design.Value)) : null;
            var reasons = new List<string>();
            var verdict = Verdict.Good;
            string label = "SAUDÁVEL";
            if (wear == null) { verdict = Verdict.Unknown; label = "SEM DADOS"; reasons.Add("A bateria não informou a capacidade de projeto ou a atual, então o desgaste não pôde ser calculado."); }
            else if (wear >= 50) { verdict = Verdict.Critical; label = "DESGASTADA"; reasons.Add($"Perdeu {wear:0}% da capacidade original: a autonomia é metade do que era. Vale trocar a bateria."); }
            else if (wear >= 30) { verdict = Verdict.Warning; label = "ATENÇÃO"; reasons.Add($"Perdeu {wear:0}% da capacidade original. Ainda funciona, mas a autonomia já caiu bastante."); }
            else if (wear >= 15) reasons.Add($"Desgaste de {wear:0}%, normal para uma bateria com uso.");
            else reasons.Add($"Desgaste de apenas {wear:0}%: bateria em ótimo estado.");
            if (cycles is >= 1000 && verdict == Verdict.Good) { verdict = Verdict.Warning; label = "ATENÇÃO"; }
            if (cycles is >= 800) reasons.Add($"{cycles} ciclos de carga: perto do fim da vida útil típica das baterias de íon-lítio (500 a 1000).");
            if (pct is < 20 && !onAc) reasons.Add("Carga baixa e fora da tomada.");

            double? volt = L("voltage_now") / 1e6;
            double? watts = L("power_now") / 1e6 ?? (L("current_now") is long ua && volt is double v ? ua / 1e6 * v : null);
            string Cap(long? x) => x == null ? "—" : energy ? $"{x / 1e6:0.0} Wh" : $"{x / 1e3:0} mAh";

            var m = new List<KeyValuePair<string, string>>();
            void Add(string k, string? val) { if (!string.IsNullOrEmpty(val)) m.Add(new(k, val)); }
            Add("Carga atual", pct != null ? $"{pct}%" : null);
            Add("Estado", status switch { "Charging" => "Carregando", "Discharging" => "Descarregando", "Full" => "Totalmente carregada", "Not charging" => "Na tomada (sem carregar)", _ => status });
            long? now = L("energy_now") ?? L("charge_now");
            if (discharging && watts is > 0.5 && now is long n && energy) Add("Autonomia estimada", TimeSpan.FromHours(n / 1e6 / watts.Value) is var t ? $"{(int)t.TotalHours}h {t.Minutes:00}min" : null);
            Add("Capacidade de projeto", design != null ? Cap(design) : null);
            Add("Capacidade máxima atual", full != null ? Cap(full) : null);
            Add("Desgaste", wear != null ? $"{wear:0.0}%" : null);
            Add("Ciclos de carga", cycles?.ToString());
            if (volt is > 0) Add("Tensão", $"{volt:0.00} V");
            if (watts is > 0) Add(charging ? "Potência de carga" : "Consumo atual", $"{watts:0.0} W");
            if (S("charge_control_end_threshold") is string th) Add("Limite de carga", th + "%");
            Add("Fabricante", S("manufacturer"));
            Add("Modelo", S("model_name"));
            Add("Química", S("technology") switch { "Li-ion" => "Íon-lítio", "Li-poly" => "Polímero de lítio", "NiMH" => "NiMH", var x => x });

            cards.Add(new InfoCard
            {
                Title = bats.Count > 1 ? $"Bateria {Path.GetFileName(b)}" : "Bateria",
                Meta = (S("manufacturer") ?? "") + (pct != null ? $" · {pct}% de carga" : ""),
                Verdict = verdict, Label = label, Reasons = string.Join(" ", reasons), Metrics = m,
            });
        }

        // Cartão de energia (existe em qualquer PC)
        var em = new List<KeyValuePair<string, string>> { new("Fonte de energia", bats.Count == 0 ? "Tomada (PC de mesa)" : onAc ? "Tomada" : "Bateria") };
        if (profile != null) em.Add(new("Perfil de energia", ProfileName(profile)));
        if (Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor") is string gov) em.Add(new("Governador da CPU", gov));
        if (Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference") is string epp) em.Add(new("Preferência de energia (EPP)", epp));
        if (sensors != null)
            foreach (var s in sensors.Where(x => x.Type == SensorType.Power && x.RawValue.GetValueOrDefault() > 0).Take(6))
                em.Add(new($"{s.Hardware} · {s.Name}", s.Value));
        string advice = profile is "balanced" or "power-saver"
            ? " Para jogos e benchmarks, o perfil Desempenho (aba Otimização) evita que o PC diminua o clock." : "";
        cards.Add(new InfoCard
        {
            Title = "Energia",
            Meta = bats.Count == 0 ? "Nenhuma bateria detectada" : "Fonte, perfil e consumo",
            Verdict = Verdict.Good, Label = "EM USO",
            Reasons = (profile != null ? $"Perfil de energia ativo: {ProfileName(profile)}." : "Sem power-profiles-daemon: o perfil segue o governador da CPU.") + advice
                      + (bats.Count == 0 ? " O consumo da fonte (PSU) não é medido pelo sistema; os valores abaixo são dos sensores de CPU, GPU e SSD." : ""),
            Metrics = em,
        });
        return cards;
    }

    public static string ProfileName(string p) => p switch
    {
        "performance" => "Desempenho", "balanced" => "Equilibrado", "power-saver" => "Economia de energia", _ => p,
    };
}
