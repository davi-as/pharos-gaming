using Avalonia.Headless.XUnit;

namespace PharosGaming.Tests;

public class ConsoleTests
{
    private const string Autostart = """
        -- Extra autostart processes.
        -- o.launch_on_start("antimicrox --hidden")
        o.launch_on_start("/home/u/.local/bin/hydra --hidden")
        o.launch_on_start("steam -silent")
        """;

    [Fact]
    public void Buttons_RoundTrip()
    {
        var d = ConsoleSetup.Gestures.ToDictionary(g => g.Key, g => g.Default);
        d["GUIDE_TAP"] = "steam_bp";
        d["GUIDE_VOLUME"] = "0";
        d["HOLD_SECONDS"] = "1.5";
        var back = ConsoleSetup.ParseEnv(ConsoleSetup.BuildButtons(d));
        Assert.Equal("steam_bp", back["GUIDE_TAP"]);
        Assert.Equal("0", back["GUIDE_VOLUME"]);
        Assert.Equal("1.5", back["HOLD_SECONDS"]);
        Assert.DoesNotContain(back.Keys, k => k.StartsWith('#'));
    }

    [Fact]
    public void Autostart_TogglesHydraAndSteamMango()
    {
        Assert.True(ConsoleSetup.LineState(Autostart, ConsoleSetup.IsHydraAutostart, "--"));
        string off = ConsoleSetup.SetLine(Autostart, ConsoleSetup.IsHydraAutostart, "--", false);
        Assert.Contains("-- o.launch_on_start(\"/home/u/.local/bin/hydra --hidden\")", off);
        Assert.False(ConsoleSetup.LineState(off, ConsoleSetup.IsHydraAutostart, "--"));
        Assert.Equal(Autostart, ConsoleSetup.SetLine(off, ConsoleSetup.IsHydraAutostart, "--", true));
        Assert.Contains("-- o.launch_on_start(\"antimicrox --hidden\")", off); // o resto fica como estava

        Assert.False(ConsoleSetup.SteamMango(Autostart));
        string on = ConsoleSetup.SetSteamMango(Autostart, true);
        Assert.Contains("o.launch_on_start(\"env MANGOHUD=1 steam -silent\")", on);
        Assert.True(ConsoleSetup.SteamMango(on));
        Assert.Equal(on, ConsoleSetup.SetSteamMango(on, true)); // não duplica
        Assert.Equal(Autostart, ConsoleSetup.SetSteamMango(on, false));
    }

    [Fact]
    public void Wrapper_MangoExport()
    {
        const string w = "#!/bin/bash\nexport APPDIR=/x\nexport MANGOHUD=1\nexec x \"$@\"";
        string off = ConsoleSetup.SetLine(w, ConsoleSetup.IsMangoExport, "#", false);
        Assert.Contains("# export MANGOHUD=1", off);
        Assert.StartsWith("#!/bin/bash", off); // o shebang não é "linha comentada"
        Assert.False(ConsoleSetup.LineState(off, ConsoleSetup.IsMangoExport, "#"));
        Assert.Null(ConsoleSetup.LineState("exec x", ConsoleSetup.IsMangoExport, "#"));
    }

    [Fact]
    public void Controllers_SkipMotionSensors()
    {
        const string devs = "N: Name=\"Pro Controller\"\nN: Name=\"Pro Controller (IMU)\"\nN: Name=\"Xbox Wireless Controller\"\nN: Name=\"Logitech USB Keyboard\"\n";
        Assert.Equal(new[] { "Pro Controller", "Xbox Wireless Controller" }, ConsoleSetup.Controllers(devs));
    }

    [Fact]
    public void Doctor_ParsesSectionsAndMarks()
    {
        const string o = "Serviços\n  \u001b[32m✓\u001b[0m xbox-guide ativo\nControles\n  \u001b[33m!\u001b[0m Xbox desconectado\n  ✗ sem BT\n\n\u001b[32mTudo certo — é só jogar.\u001b[0m\n";
        var c = ConsoleSetup.ParseDoctor(o);
        Assert.Equal(3, c.Count);
        Assert.Equal(("Serviços", '✓', "xbox-guide ativo"), (c[0].Section, c[0].Mark, c[0].Text));
        Assert.Equal(('!', "Controles"), (c[1].Mark, c[1].Section));
        Assert.Equal('✗', c[2].Mark);
    }

    [Fact]
    public void Mango_HiddenAndKeys()
    {
        const string conf = "legacy_layout=0\ntoggle_hud=Control_L+Shift_L+Q\nno_display=1\nfps\n";
        Assert.True(ConsoleSetup.MangoHidden(conf));
        string shown = ConsoleSetup.SetMangoHidden(conf, false);
        Assert.False(ConsoleSetup.MangoHidden(shown));
        Assert.Contains("fps", shown.Split('\n'));
        Assert.True(ConsoleSetup.MangoHidden(ConsoleSetup.SetMangoHidden(shown, true)));
        Assert.Equal("Ctrl + Shift + Q", ConsoleSetup.MangoToggleKeys(conf));
        Assert.Equal("Shift + F12", ConsoleSetup.MangoToggleKeys("fps"));
    }
}

/// <summary>Overlay do PHAROS grava o MangoHud.conf e devolve o do usuário (XDG_CONFIG_HOME temporário do TestApp).</summary>
public class OverlayFileTests
{
    [AvaloniaFact]
    public void OverlayOn_BacksUpUserConf_OffRestoresIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MangoHudConfig.FilePath)!);
        File.WriteAllText(MangoHudConfig.FilePath, "toggle_hud=Control_L+Shift_L+Q\nno_display=1\n");
        var s = new AppSettings { OverlayManaged = true, OverlayVisible = true };
        MangoHudConfig.Write(s);
        Assert.True(MangoHudConfig.IsOurs);
        Assert.Contains("no_display=1", File.ReadAllText(MangoHudConfig.BackupPath));
        MangoHudConfig.Write(s); // de novo: o backup continua sendo o do usuário
        Assert.Contains("no_display=1", File.ReadAllText(MangoHudConfig.BackupPath));
        s.OverlayManaged = false;
        MangoHudConfig.Write(s);
        Assert.Equal("toggle_hud=Control_L+Shift_L+Q\nno_display=1\n", File.ReadAllText(MangoHudConfig.FilePath));
        Assert.False(File.Exists(MangoHudConfig.BackupPath));
    }
}
