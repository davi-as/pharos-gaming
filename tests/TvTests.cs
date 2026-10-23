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
