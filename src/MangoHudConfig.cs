namespace PharosGaming;

/// <summary>
/// Overlay do PHAROS no Linux = MangoHud com um arquivo de configuração próprio (~/.config/pharos-gaming/MangoHud.conf).
/// O MangoHud.conf do usuário não é tocado: os jogos abertos pelo PHAROS recebem MANGOHUD_CONFIGFILE apontando para cá
/// quando o overlay do PHAROS está ligado. O MangoHud relê o arquivo sozinho, então as mudanças valem com o jogo aberto.
/// </summary>
public static class MangoHudConfig
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "MangoHud.conf");

    /// <summary>Opções do MangoHud de cada item do catálogo (OverlayMetrics).</summary>
    private static readonly Dictionary<string, string[]> Options = new()
    {
        ["fps"] = new[] { "fps" },
        ["frametime"] = new[] { "frametime" },
        ["low1"] = new[] { "fps_metrics=avg,0.01" },
        ["graph"] = new[] { "frame_timing" },
        ["gpu_load"] = new[] { "gpu_stats" },
        ["gpu_temp"] = new[] { "gpu_temp" },
        ["gpu_hotspot"] = new[] { "gpu_junction_temp" },
        ["gpu_clock"] = new[] { "gpu_core_clock" },
        ["gpu_memclock"] = new[] { "gpu_mem_clock" },
        ["gpu_power"] = new[] { "gpu_power" },
        ["gpu_volt"] = new[] { "gpu_voltage" },
        ["gpu_fan"] = new[] { "gpu_fan" },
        ["gpu_perfcap"] = new[] { "throttling_status" },
        ["vram_used"] = new[] { "vram" },
        ["vram_temp"] = new[] { "gpu_mem_temp" },
        ["cpu_load"] = new[] { "cpu_stats" },
        ["cpu_maxcore"] = new[] { "core_load" },
        ["cpu_temp"] = new[] { "cpu_temp" },
        ["cpu_clock"] = new[] { "cpu_mhz" },
        ["cpu_power"] = new[] { "cpu_power" },
        ["ram_used"] = new[] { "ram" },
        ["swap"] = new[] { "swap" },
        ["game"] = new[] { "exec_name" },
        ["vulkan"] = new[] { "vulkan_driver", "engine_version" },
        ["gamemode"] = new[] { "gamemode" },
        ["clock"] = new[] { "time" },
    };

    /// <summary>Canto (TL/TR/BL/BR, como no PHAROS para Windows) → position do MangoHud.</summary>
    public static string Position(string corner) => corner switch
    {
        "TR" => "top-right", "BL" => "bottom-left", "BR" => "bottom-right", _ => "top-left",
    };

    public static string Build(IEnumerable<string> keys, string corner, double scale, bool visible)
    {
        var lines = new List<string>
        {
            "# Gerado pelo PHAROS GAMING (aba Overlay). Edições manuais são substituídas.",
            "legacy_layout=0",                 // uma linha por componente, como o overlay do PHAROS
            "horizontal=0",
            $"position={Position(corner)}",
            $"font_size={Math.Round(24 * scale):0}",
            "background_alpha=0.6",
            "round_corners=6",
            "text_color=F5F5F5",
            "gpu_color=F5F5F5", "cpu_color=F5F5F5", "vram_color=F5F5F5", "ram_color=F5F5F5", "engine_color=F5F5F5",
            "frametime_color=F5F5F5", "background_color=0E0E0E",
            "toggle_hud=Control_L+Shift_L+O",  // mesmo atalho do overlay no Windows
        };
        if (!visible) lines.Add("no_display");
        var set = keys.ToHashSet();
        foreach (var m in OverlayMetrics.All)
            if (set.Contains(m.Key) && Options.TryGetValue(m.Key, out var opts)) lines.AddRange(opts);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Grava o arquivo com as escolhas atuais (chamado a cada mudança na aba Overlay).</summary>
    public static void Write(AppSettings s)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(FilePath, Build(s.OverlayItems ?? (IEnumerable<string>)OverlayMetrics.Defaults(), s.OverlayCorner, s.OverlayScale, s.OverlayVisible));
    }

    /// <summary>Prefixo das opções de inicialização: aponta o MangoHud para o arquivo do PHAROS quando o overlay está ligado.</summary>
    public static string EnvPrefix => AppSettings.Current.OverlayEnabled ? $"MANGOHUD_CONFIGFILE=\"{FilePath}\" " : "";
}
