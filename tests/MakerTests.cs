namespace PharosGaming.Tests;

public class MakerTests
{
    /// <summary>Árvore mínima do sysfs: tty → interface USB → dispositivo USB (idVendor...), driver e /dev/serial/by-id.</summary>
    private static (string Tty, string Dev) Fake()
    {
        string root = Directory.CreateTempSubdirectory("sysfs").FullName;
        string usb = Path.Combine(root, "devices/usb1/1-2"), intf = Path.Combine(usb, "1-2:1.0/ttyUSB0"), drv = Path.Combine(root, "bus/usb-serial/drivers/ch341-uart");
        Directory.CreateDirectory(intf); Directory.CreateDirectory(drv);
        File.WriteAllText(Path.Combine(usb, "idVendor"), "1a86\n");
        File.WriteAllText(Path.Combine(usb, "idProduct"), "7523\n");
        File.WriteAllText(Path.Combine(usb, "product"), "USB Serial\n");
        Directory.CreateSymbolicLink(Path.Combine(intf, "driver"), drv);
        string tty = Path.Combine(root, "class/tty");
        Directory.CreateDirectory(Path.Combine(tty, "ttyUSB0"));
        Directory.CreateSymbolicLink(Path.Combine(tty, "ttyUSB0/device"), intf);
        string dev = Path.Combine(root, "dev");
        Directory.CreateDirectory(Path.Combine(dev, "serial/by-id"));
        File.WriteAllText(Path.Combine(dev, "ttyUSB0"), "");
        File.CreateSymbolicLink(Path.Combine(dev, "serial/by-id/usb-1a86_USB_Serial-if00-port0"), Path.Combine(dev, "ttyUSB0"));
        return (tty, dev);
    }

    [Fact]
    public void List_RecognizesCh340DriverAndStableName()
    {
        var (tty, dev) = Fake();
        var holders = new Dictionary<string, List<(int, string)>> { ["/dev/ttyUSB0"] = new() { (4242, "arduino-ide") } };
        var ports = SerialPorts.List(tty, dev, new HashSet<string> { "wheel" }, holders);
        var p = Assert.Single(ports);
        Assert.Equal("/dev/ttyUSB0", p.Dev);
        Assert.Equal("CH340 / CH341", p.Chip?.Name);
        Assert.Equal("ch341-uart", p.Driver);
        Assert.EndsWith("usb-1a86_USB_Serial-if00-port0", p.ById);
        Assert.False(p.Ok);                                         // presa pela IDE
        Assert.Contains("arduino-ide (4242)", p.Diagnosis);
    }

    [Fact]
    public void GroupAccess()
    {
        var (tty, dev) = Fake();
        string group = SerialPorts.List(tty, dev, new HashSet<string>(), new()).Single().Group; // dono do arquivo de teste
        Assert.True(SerialPorts.List(tty, dev, new HashSet<string> { group }, new()).Single().UserHasAccess);
        Assert.False(SerialPorts.List(tty, dev, new HashSet<string> { "outro" }, new()).Single().UserHasAccess);
    }

    [Fact]
    public void Scripts_Quoted()
    {
        Assert.Equal($"usermod -aG 'uucp' '{Environment.UserName}'\n", SerialPorts.AddToGroupScript("uucp"));
        Assert.Equal("modprobe 'ch341'\n", SerialPorts.LoadModuleScript("ch341"));
        // cdc_acm não serve de teste: o kernel Azure do runner do CI não o traz (fica em linux-modules-extra)
        Assert.False(SerialPorts.ModuleAvailable("modulo_que_nao_existe"));
    }
}
