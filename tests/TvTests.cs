using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace PharosGaming.Tests;

public class GamepadTests
{
    [Fact]
    public void Map_ButtonsHatAndStick()
    {
        var s = new Gamepad.Stick();
        Assert.Equal(PadNav.Confirm, Gamepad.Map(1, 304, 1, s));     // A (embaixo)
        Assert.Equal(PadNav.Back, Gamepad.Map(1, 305, 1, s));        // B (direita)
        Assert.Null(Gamepad.Map(1, 304, 0, s));                      // soltar não conta
        Assert.Equal(PadNav.Up, Gamepad.Map(3, 17, -1, s));          // direcional do Xbox (hat)
        Assert.Equal(PadNav.Right, Gamepad.Map(1, 547, 1, s));       // direcional do 8BitDo (botões)
        Assert.Equal(PadNav.Left, Gamepad.Map(3, 0, -30000, s));     // analógico
        Assert.Null(Gamepad.Map(3, 0, -31000, s));                   // segurando: não repete
        Assert.Null(Gamepad.Map(3, 0, 0, s));                        // volta ao centro rearma
        Assert.Equal(PadNav.Right, Gamepad.Map(3, 0, 25000, s));
        Assert.Null(Gamepad.Map(3, 1, 15000, s));                    // pouco: nada
    }

    [Fact]
    public void Devices_OnlyJoysticksWithoutImu()
    {
        const string devs = "I: Bus=0005\nN: Name=\"Pro Controller\"\nH: Handlers=event21 js0\n\nN: Name=\"Pro Controller (IMU)\"\nH: Handlers=event22 js1\n\nN: Name=\"Teclado\"\nH: Handlers=sysrq kbd event3 leds\n";
        Assert.Equal(new[] { "/dev/input/event21" }, Gamepad.Devices(devs));
    }
}

public class TvWindowTests
{
    [AvaloniaFact]
    public void Opens_NavigatesWithArrows_ClosesWithEscape()
    {
        var w = new TvWindow();
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var tiles = w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();
        var shot = tiles.First(b => b.IsEffectivelyEnabled && ((b.Content as StackPanel)?.Children[0] as TextBlock)?.Text == "CAPTURA");
        var bp = tiles.First(b => ((b.Content as StackPanel)?.Children[0] as TextBlock)?.Text == "HYDRA");
        var close = w.FindControl<Button>("BtnClose")!;
        shot.Focus();
        w.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(bp.IsFocused);
        w.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(close.IsFocused);
        bool closed = false;
        w.Closed += (_, _) => closed = true;
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(closed);
    }
}

public class LsfgTests

{
    [Fact]
    public void Build_OneProfilePerGameWithExe()
    {
        var toml = Lsfg.Build(new Dictionary<string, int> { ["wine-eldenring"] = 2, ["retroarch"] = 3, ["wine-hades"] = 1 }, "/h/.local/share/pharos-gaming/lsfg-vk.dll");
        Assert.Contains("version = 2", toml);
        Assert.Contains("dll = \"/h/.local/share/pharos-gaming/lsfg-vk.dll\"", toml);
        Assert.Contains("active_in = [ \"eldenring.exe\" ]", toml);
        Assert.Contains("active_in = [ \"retroarch\" ]", toml);
        Assert.Contains("multiplier = 3", toml);
        Assert.DoesNotContain("hades", toml); // 1× = desligada: sem perfil
        Assert.Equal(2, toml.Split("[[profile]]").Length - 1);
    }

    [Fact]
    public void Build_KeepsConfigValidWithNoGames() =>
        Assert.Contains("active_in = [ \"pharos-nenhum-jogo\" ]", Lsfg.Build(new Dictionary<string, int>(), "/x.dll"));

    [AvaloniaFact] // grava configs: XDG_CONFIG_HOME temporário do TestApp
    public void Settle_ChangesLimitOnlyWithGameClosed()
    {
        var s = new AppSettings();
        s.FpsLimits["wine-eldenring"] = 59.95;
        Lsfg.Set(s, "wine-eldenring", 2, 59.95);
        Assert.True(Lsfg.Pending(s, "wine-eldenring"));
        Assert.Empty(Lsfg.Settle(s, new HashSet<string> { "wine-eldenring" })); // aberto: o limite fica
        Assert.Equal(59.95, s.FpsLimits["wine-eldenring"]);
        Assert.Single(Lsfg.Settle(s, new HashSet<string>()));                    // fechou: 29,98 reais × 2
        Assert.Equal(29.98, s.FpsLimits["wine-eldenring"]);
        Lsfg.Set(s, "wine-eldenring", 1, 59.95);
        Lsfg.Settle(s, new HashSet<string>());
        Assert.Equal(59.95, s.FpsLimits["wine-eldenring"]);
        Assert.False(Lsfg.Pending(s, "wine-eldenring"));
    }
}

public class QuickActionsTests
{
    private const string Status = "PipeWire 'pipewire-0' [1.4.8]\n └─ Clients:\n\nAudio\n ├─ Devices:\n │      64. Fosi Audio SK02                     [alsa]\n │  \n ├─ Sinks:\n │  *   50. Fosi Audio SK02 Analog Stereo       [vol: 0.50]\n │      55. GA106 High Definition Audio Controller Digital Stereo (HDMI 2) [vol: 1.00]\n │      57. Starship/Matisse HD Audio Controller Digital Stereo (IEC958) [vol: 0.40]\n │  \n ├─ Sources:\n │      70. Microfone                          [vol: 1.00]\n\nVideo\n ├─ Sinks:\n │      90. Câmera                             [vol: 1.00]\n";

    [Fact]
    public void Sinks_OnlyAudioWithDefault()
    {
        var s = QuickActions.Sinks(Status);
        Assert.Equal(new[] { 50, 55, 57 }, s.Select(x => x.Id));
        Assert.True(s[0].Default);
        Assert.Equal("Fosi SK02", QuickActions.Short(s[0].Name));
        Assert.Equal("TV (HDMI)", QuickActions.Short(s[1].Name));
        Assert.Equal("Óptica", QuickActions.Short(s[2].Name));
        Assert.Equal(50, QuickActions.ParseVolume("Volume: 0.50"));
        Assert.Equal(35, QuickActions.ParseVolume("Volume: 0.35 [MUTED]"));
    }

    [Fact]
    public void PadBatteries_PercentOrLevel()
    {
        var root = Directory.CreateTempSubdirectory("ps").FullName;
        void Dev(string n, string scope, string? cap, string? level)
        {
            var d = Directory.CreateDirectory(Path.Combine(root, n)).FullName;
            File.WriteAllText(Path.Combine(d, "scope"), scope);
            if (cap != null) File.WriteAllText(Path.Combine(d, "capacity"), cap);
            if (level != null) File.WriteAllText(Path.Combine(d, "capacity_level"), level);
        }
        Dev("nintendo_switch_controller_battery_0005", "Device", null, "Low");
        Dev("xpadneo_battery_0", "Device", "80", "Normal");
        Dev("BAT0", "System", "50", "Normal"); // bateria do notebook: fora
        var b = QuickActions.PadBatteries(root).OrderBy(x => x.Name).ToList();
        Assert.Equal(new[] { "8BitDo baixa", "Xbox 80%" }, b.Select(QuickActions.Text));
        Assert.True(QuickActions.Low(b[0]));
        Assert.False(QuickActions.Low(b[1]));
    }

    [Fact]
    public void Choices_KeepAtLeast30Real()
    {
        Assert.Equal(new[] { 2 }, Lsfg.Choices(59.95));
        Assert.Equal(new[] { 2, 3, 4 }, Lsfg.Choices(165));
        Assert.Contains("performance_mode = true", Lsfg.Build(new Dictionary<string, int> { ["wine-x"] = 2 }, "/d", new HashSet<string> { "wine-x" }));
    }
}
