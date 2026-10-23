using System.Text.Json;

namespace PharosGaming;

/// <summary>
/// Saúde dos discos: lsblk (modelo, barramento, partições), udisks (S.M.A.R.T. básico sem root: temperatura, horas, falha
/// prevista, setores ruins) e, quando o usuário pede, smartctl como root (desgaste do SSD, total gravado, erros).
/// Nada é escrito no disco.
/// </summary>
public static class DiskHealth
{
    public sealed class Raw
    {
        public string Name = "", Model = "", Serial = "", Firmware = "", Bus = "", Media = "";
        public long Size;
        public bool Removable;
        public bool? PredictFail, SmartPassed;
        public double? Temp, Wear;
        public long? PowerOnHours, BadSectors, MediaErrors, Reallocated, Pending, Uncorrectable, UnsafeShutdowns, PowerCycles, CriticalWarning;
        public double? WrittenGb, ReadGb;
        public string Mounts = "";
        public bool Full; // leu o S.M.A.R.T. completo (smartctl)
    }

    /// <summary>Lê o que dá sem root. <paramref name="smartJson"/> (saída do smartctl por dispositivo) completa quando houver.</summary>
    public static List<InfoCard> Read(IReadOnlyDictionary<string, string>? smartJson = null)
    {
        List<BlockDev> devs;
        try { devs = SystemInfo.ReadBlockDevices(); }
        catch (Exception ex) { return new() { new InfoCard { Title = "Não foi possível ler os discos", Reasons = ex.Message } }; }
        var udisks = ParseUdisksDump(Sys.RunAsync("udisksctl", "dump").GetAwaiter().GetResult().Out);
        var raws = new List<Raw>();
        foreach (var d in devs.Where(d => d.Type == "disk" && d.Size > 0 && !d.Name.StartsWith("zram") && !d.Name.StartsWith("loop")))
        {
            var r = FromBlock(d);
            var u = udisks.Values.FirstOrDefault(p => d.Serial != null && p.GetValueOrDefault("Serial") == d.Serial);
            if (u != null) ApplyUdisks(r, u);
            if (smartJson != null && smartJson.TryGetValue("/dev/" + d.Name, out var json)) ApplySmartctl(r, json);
            raws.Add(r);
        }
        return raws.Select(ToCard).ToList();
    }

    public static Raw FromBlock(BlockDev d)
    {
        var mounts = new List<string>();
        void Walk(BlockDev b) { mounts.AddRange(b.Mounts.Where(m => m.StartsWith('/'))); foreach (var c in b.Children) Walk(c); }
        Walk(d);
        return new Raw
        {
            Name = d.Name, Model = d.Model ?? d.Name, Serial = d.Serial ?? "", Firmware = d.Rev ?? "", Size = d.Size,
            Bus = d.Tran?.ToUpperInvariant() is { Length: > 0 } t ? t : d.Name.StartsWith("nvme") ? "NVME" : "",
            Media = d.Name.StartsWith("nvme") || !d.Rotational ? "SSD" : "HDD",
            Removable = d.Removable || d.Tran == "usb",
            Mounts = string.Join(" ", mounts.OrderBy(m => m.Length).Take(3)),
        };
    }

    // ---------------- udisks ----------------

    /// <summary>"udisksctl dump" → objeto → propriedades (as interfaces são achatadas: os nomes não se repetem entre elas).</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseUdisksDump(string text)
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("/org/freedesktop/UDisks2/drives/"))
            {
                cur = new Dictionary<string, string>();
                all[line.TrimEnd(':', ' ')] = cur;
            }
            else if (line.StartsWith('/')) cur = null; // block_devices, jobs...: não interessam
            else if (cur != null && line.StartsWith("    ") && line.IndexOf(':') is int i and > 0)
                cur[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return all;
    }

    public static void ApplyUdisks(Raw r, Dictionary<string, string> p)
    {
        static double? D(Dictionary<string, string> p, string k) =>
            double.TryParse(p.GetValueOrDefault(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
        if (D(p, "SmartTemperature") is double k && k > 200) r.Temp = k - 273.15; // udisks informa em kelvin
        if (D(p, "SmartPowerOnHours") is double h) r.PowerOnHours = (long)h;
        else if (D(p, "SmartPowerOnSeconds") is double s && s > 0) r.PowerOnHours = (long)(s / 3600);
        if (p.GetValueOrDefault("SmartFailing") is string f && f.Length > 0) r.PredictFail = f == "true";
        if (D(p, "SmartNumBadSectors") is double bad) r.BadSectors = (long)bad;
        // NVMe: lista de avisos críticos ("spare", "temperature", "degraded", "readonly", "volatile_mem"...)
        if (p.TryGetValue("SmartCriticalWarning", out var cw)) r.PredictFail = (r.PredictFail ?? false) || cw.Trim().Length > 0;
    }

    // ---------------- smartctl ----------------

    /// <summary>Script de root que roda o smartctl em cada disco, separando as saídas por uma linha "### /dev/xxx".</summary>
    public static string SmartctlScript(IEnumerable<string> devices) =>
        string.Concat(devices.Select(d => $"echo {Sys.Quote("### " + d)}; smartctl -j -a {Sys.Quote(d)}\n"));

    public static Dictionary<string, string> SplitSmartctl(string output)
    {
        var map = new Dictionary<string, string>();
        string? dev = null;
        var sb = new System.Text.StringBuilder();
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("### "))
            {
                if (dev != null) map[dev] = sb.ToString();
                dev = line[4..].Trim();
                sb.Clear();
            }
            else sb.AppendLine(line);
        }
        if (dev != null) map[dev] = sb.ToString();
        return map;
    }

    public static void ApplySmartctl(Raw r, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { return; }
        using (doc)
        {
            var root = doc.RootElement;
            static long? L(JsonElement e, params string[] path)
            {
                foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
                return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) ? v : null;
            }
            r.Full = true;
            if (root.TryGetProperty("smart_status", out var ss) && ss.TryGetProperty("passed", out var passed))
                r.SmartPassed = passed.GetBoolean();
            if (L(root, "temperature", "current") is long t) r.Temp = t;
            if (L(root, "power_on_time", "hours") is long poh) r.PowerOnHours = poh;
            if (L(root, "power_cycle_count") is long pc) r.PowerCycles = pc;

            if (root.TryGetProperty("nvme_smart_health_information_log", out var nv))
            {
                r.Wear = L(nv, "percentage_used");
                r.MediaErrors = L(nv, "media_errors");
                r.UnsafeShutdowns = L(nv, "unsafe_shutdowns");
                r.CriticalWarning = L(nv, "critical_warning");
                // "data units" do NVMe = 1000 setores de 512 bytes
                if (L(nv, "data_units_written") is long w) r.WrittenGb = w * 512000.0 / 1e9;
                if (L(nv, "data_units_read") is long rd) r.ReadGb = rd * 512000.0 / 1e9;
            }
            if (root.TryGetProperty("ata_smart_attributes", out var ata) && ata.TryGetProperty("table", out var table))
            {
                foreach (var a in table.EnumerateArray())
                {
                    long id = L(a, "id") ?? 0, raw = L(a, "raw", "value") ?? 0, value = L(a, "value") ?? 0;
                    switch (id)
                    {
                        case 5: r.Reallocated = raw; break;
                        case 197: r.Pending = raw; break;
                        case 198: r.Uncorrectable = raw; break;
                        case 192: r.UnsafeShutdowns = raw; break;
                        case 177 or 231 or 233 or 169: r.Wear ??= Math.Clamp(100 - value, 0, 100); break; // valor normalizado = vida restante
                        case 241: r.WrittenGb = raw * 512.0 / 1e9; break;  // LBAs gravados
                        case 242: r.ReadGb = raw * 512.0 / 1e9; break;
                    }
                }
            }
        }
    }

    // ---------------- veredito ----------------

    public static InfoCard ToCard(Raw r)
    {
        bool isSsd = r.Media == "SSD";
        var crit = new List<string>();
        var warn = new List<string>();
        bool anyData = r.PredictFail != null || r.SmartPassed != null || r.Wear != null || r.Temp != null || r.PowerOnHours != null;

        if (r.PredictFail == true || r.SmartPassed == false) crit.Add("O próprio disco avisa (S.M.A.R.T.) que deve falhar em breve. Faça backup agora.");
        if (r.CriticalWarning is > 0) crit.Add($"O SSD reporta aviso crítico (código {r.CriticalWarning}): reserva esgotada, superaquecimento ou modo somente leitura.");
        if (r.Wear is double w)
        {
            if (w >= 95) crit.Add($"Desgaste de {w:0}%: o SSD está no fim da vida útil. Troque e faça backup.");
            else if (w >= 80) warn.Add($"Desgaste de {w:0}%: o SSD já gastou boa parte da vida útil. Planeje a troca.");
        }
        if (r.Temp is double t)
        {
            double okMax = isSsd ? 70 : 55, critMax = isSsd ? 85 : 65;
            if (t >= critMax) crit.Add($"Temperatura de {t:0} °C é muito alta para este disco.");
            else if (t >= okMax) warn.Add($"Temperatura de {t:0} °C está alta. Melhore a ventilação.");
        }
        long bad = Math.Max(r.BadSectors ?? 0, (r.Reallocated ?? 0) + (r.Pending ?? 0));
        if (bad > 0) warn.Add($"{bad:N0} setores ruins ou realocados: o disco está se desgastando. Faça backup e acompanhe se o número cresce.");
        if (r.Uncorrectable > 0 || r.MediaErrors > 0)
            warn.Add($"Erros de mídia não corrigidos: {Math.Max(r.Uncorrectable ?? 0, r.MediaErrors ?? 0):N0}.");

        Verdict v = crit.Count > 0 ? Verdict.Critical : warn.Count > 0 ? Verdict.Warning : anyData ? Verdict.Good : Verdict.Unknown;
        var reasons = crit.Concat(warn).ToList();
        if (reasons.Count == 0)
            reasons.Add(v == Verdict.Good
                ? "Nenhum problema encontrado nas leituras disponíveis." + (r.Full ? "" : " Para ver desgaste e total gravado, use LEITURA COMPLETA.")
                : "Este disco não informa dados de saúde (comum em pendrives e alguns HDs externos).");

        var metrics = new List<KeyValuePair<string, string>>();
        void M(string k, string? val) { if (val != null) metrics.Add(new(k, val)); }
        M("Temperatura", r.Temp is double tt ? $"{tt:0} °C" : null);
        if (r.Wear is double wr) { M("Desgaste", $"{wr:0}%"); M("Vida útil restante", $"{Math.Max(0, 100 - wr):0}%"); }
        M("Horas ligado", r.PowerOnHours is long h ? $"{h:N0} h ({h / 24.0:N0} dias)" : null);
        M("Ciclos liga/desliga", r.PowerCycles is long pc ? $"{pc:N0}" : null);
        M("Desligamentos inseguros", r.UnsafeShutdowns is long us ? $"{us:N0}" : null);
        M("Setores ruins / realocados", r.BadSectors != null || r.Reallocated != null ? $"{bad:N0}" : null);
        M("Erros de mídia", r.MediaErrors is long me ? $"{me:N0}" : r.Uncorrectable is long un ? $"{un:N0}" : null);
        M("Total gravado", r.WrittenGb is double wg && wg > 0 ? (wg >= 1000 ? $"{wg / 1000:N1} TB" : $"{wg:N0} GB") : null);
        M("Total lido", r.ReadGb is double rg && rg > 0 ? (rg >= 1000 ? $"{rg / 1000:N1} TB" : $"{rg:N0} GB") : null);
        M("Falha prevista (S.M.A.R.T.)", r.PredictFail is bool pf ? (pf ? "SIM" : "Não") : r.SmartPassed is bool sp ? (sp ? "Não" : "SIM") : null);
        M("Firmware", r.Firmware.Length > 0 ? r.Firmware : null);
        M("Dispositivo", "/dev/" + r.Name);

        var meta = new[] { r.Media, r.Bus, Sys.Bytes(r.Size, si: true), r.Mounts.Length > 0 ? "Montado em " + r.Mounts : "" }.Where(x => !string.IsNullOrEmpty(x));
        return new InfoCard
        {
            Title = r.Model,
            Meta = string.Join(" · ", meta),
            Verdict = r.Removable && !anyData ? Verdict.Unknown : v,
            Reasons = string.Join(" ", reasons),
            Metrics = metrics,
        };
    }
}
