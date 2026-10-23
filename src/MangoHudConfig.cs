namespace PharosGaming;

/// <summary>
/// Overlay do PHAROS no Linux = o MangoHud.conf (~/.config/MangoHud), que todo jogo lê: os do Hydra e os da Steam
/// recebem MANGOHUD=1 (PC como console), sem opção de inicialização. Ligado, o PHAROS guarda o arquivo do usuário
/// (GOverlay, por exemplo) e grava o dele; desligado, devolve o original. O MangoHud relê o arquivo com o jogo aberto.
/// </summary>
public static class MangoHudConfig
{
    public static string FilePath => Path.Combine(Sys.ConfigHome, "MangoHud", "MangoHud.conf");
    public static string BackupPath => Path.Combine(AppSettings.Folder, "MangoHud.usuario.conf");
    private const string Header = "# Gerado pelo PHAROS GAMING (aba Overlay).";

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
            Header + " Edições manuais são substituídas; o seu arquivo volta ao desligar o overlay do PHAROS.",
            "legacy_layout=0",                 // uma linha por componente, como o overlay do PHAROS
            "horizontal=0",
            $"position={Position(corner)}",
            $"font_size={Math.Round(24 * scale):0}",
            "background_alpha=0.6",
            "round_corners=6",
            "text_color=F5F5F5",
            "gpu_color=F5F5F5", "cpu_color=F5F5F5", "vram_color=F5F5F5", "ram_color=F5F5F5", "engine_color=F5F5F5",
            "frametime_color=F5F5F5", "background_color=0E0E0E",
            "toggle_hud=Control_L+Shift_L+Q",  // o mesmo do MangoHud.conf do usuário e do View+Start do controle
        };
        if (!visible) lines.Add("no_display");
        // log que o painel do PHAROS lê (o MangoHud só começa a gravar com o overlay visível)
        lines.AddRange(new[] { "autostart_log=1", "log_interval=250", $"output_folder={MangoHudFrames.OwnFolder}" });
        var set = keys.ToHashSet();
        foreach (var m in OverlayMetrics.All)
            if (set.Contains(m.Key) && Options.TryGetValue(m.Key, out var opts)) lines.AddRange(opts);
        return string.Join("\n", lines) + "\n";
    }

    public static bool IsOurs => File.Exists(FilePath) && File.ReadLines(FilePath).FirstOrDefault()?.StartsWith(Header) == true;

    /// <summary>Aplica a aba Overlay: ligado grava o arquivo do PHAROS (guardando o do usuário uma vez); desligado devolve o original.</summary>
    public static void Write(AppSettings s)
    {
        if (s.OverlayManaged)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            Directory.CreateDirectory(AppSettings.Folder);
            if (File.Exists(FilePath) && !IsOurs) File.Copy(FilePath, BackupPath, overwrite: true);
            // troca atômica: o MangoHud relê no IN_DELETE_SELF e nunca vê o arquivo pela metade
            File.WriteAllText(FilePath + ".tmp", Build(s.OverlayItems ?? (IEnumerable<string>)OverlayMetrics.Defaults(), s.OverlayCorner, s.OverlayScale, s.OverlayVisible));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        else if (IsOurs)
        {
            if (File.Exists(BackupPath)) File.Move(BackupPath, FilePath, overwrite: true);
            else File.Delete(FilePath); // o usuário não tinha MangoHud.conf
        }
    }
}
