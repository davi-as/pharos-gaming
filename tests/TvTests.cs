using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

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
        var bp = w.FindControl<Button>("BtnBp")!;
        var close = w.FindControl<Button>("BtnClose")!;
        bp.Focus();
        w.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.True(close.IsFocused);
        w.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.True(bp.IsFocused);
        bool closed = false;
        w.Closed += (_, _) => closed = true;
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(closed);
    }
}
