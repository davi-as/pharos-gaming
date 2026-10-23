using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PharosGaming.Tests;

public class OpenRgbTests
{
    private static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); w.Write((ushort)b.Length); w.Write(b); }

    /// <summary>Dados de um controlador no protocolo 3: 2 modos (ativo = 1), 1 zona, <paramref name="leds"/> LEDs.</summary>
    public static byte[] Controller(string name, int leds)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0u); w.Write(0);
        Str(w, name); Str(w, "MSI"); Str(w, "desc"); Str(w, "1.0"); Str(w, "SN"); Str(w, "loc");
        w.Write((ushort)2); w.Write(1);
        for (int m = 0; m < 2; m++)
        {
            Str(w, m == 0 ? "Direct" : "Rainbow"); w.Write(m); w.Write(0u);
            for (int k = 0; k < 10; k++) w.Write((uint)k);
            w.Write((ushort)1); w.Write(new byte[] { 1, 2, 3, 0 });
        }
        w.Write((ushort)1); Str(w, "Zona"); w.Write(0); w.Write(0u); w.Write(0u); w.Write(0u); w.Write((ushort)0);
        w.Write((ushort)leds);
        return ms.ToArray();
    }

    [Fact]
    public void ParseController_KeepsActiveModeForRestore()
    {
        var (name, vendor, leds, active, mode) = OpenRgbClient.ParseController(Controller("Mystic Light", 12));
        Assert.Equal(("Mystic Light", "MSI", 12, 1), (name, vendor, leds, active));
        Assert.NotNull(mode);
        Assert.Contains((byte)'R', mode!); // bytes do modo "Rainbow" (o ativo)
    }

    [Fact]
    public void Packets_HeaderAndLedPayload()
    {
        var p = OpenRgbClient.Packet(3, 1050, new byte[] { 9 });
        Assert.Equal("ORGB", Encoding.ASCII.GetString(p, 0, 4));
        Assert.Equal(3u, BitConverter.ToUInt32(p, 4));
        Assert.Equal(1050u, BitConverter.ToUInt32(p, 8));
        Assert.Equal(1u, BitConverter.ToUInt32(p, 12));
        var leds = OpenRgbClient.LedsPayload(new (byte, byte, byte)[] { (255, 0, 0), (0, 255, 0), (0, 0, 255) }, ledCount: 2);
        Assert.Equal(6 + 8, leds.Length);                          // corta no número de LEDs do dispositivo
        Assert.Equal(2, BitConverter.ToUInt16(leds, 4));
        Assert.Equal(new byte[] { 0, 255, 0, 0 }, leds[10..14]);
    }

    [Fact]
    public async Task Connect_TalksToServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var head = new byte[16];
            void Reply(uint type, byte[] data) { s.Write(OpenRgbClient.Packet(0, type, data)); }
            for (int i = 0; i < 4; i++)
            {
                await s.ReadExactlyAsync(head);
                uint type = BitConverter.ToUInt32(head, 8), size = BitConverter.ToUInt32(head, 12);
                if (size > 0) await s.ReadExactlyAsync(new byte[size]);
                if (type == 40) Reply(40, BitConverter.GetBytes(4u));            // servidor mais novo: fica no 3
                else if (type == 0) Reply(0, BitConverter.GetBytes(1u));
                else if (type == 1) Reply(1, Controller("RAM", 8));
            }
        });
        using var orgb = new OpenRgbClient();
        var (devs, status) = orgb.Connect("127.0.0.1", port);
        await server;
        listener.Stop();
        Assert.Equal(3, orgb.Protocol);
        Assert.Single(devs);
        Assert.Equal(8, devs[0].LedCount);
        Assert.Contains("1 dispositivo", status);
    }

    [Fact]
    public void Connect_NoServerExplains()
    {
        using var orgb = new OpenRgbClient();
        var (devs, status) = orgb.Connect("127.0.0.1", 1); // porta fechada
        Assert.Empty(devs);
        Assert.Contains("OpenRGB", status);
    }

    [Fact]
    public void Effects_StaticRainbowAndLive()
    {
        var x = new[] { 0.0, 0.5, 1.0 };
        Assert.All(LightEffects.Frame("static", 0, x, (200, 100, 50), 0.5, 5, null), c => Assert.Equal((100, 50, 25), (c.R, c.G, c.B)));
        var rb = LightEffects.Frame("rainbow", 0, x, (0, 0, 0), 1, 5, null);
        Assert.Equal((255, 0, 0), (rb[0].R, rb[0].G, rb[0].B));      // matiz 0 = vermelho
        Assert.Equal((0, 255, 255), (rb[1].R, rb[1].G, rb[1].B));    // matiz 180 = ciano
        Assert.Equal((0, 0, 255), LightEffects.LiveColor("cpu", 30));  // frio = azul
        Assert.Equal((255, 0, 0), LightEffects.LiveColor("cpu", 90));  // quente = vermelho
        Assert.All(LightEffects.Frame("off", 1, x, (255, 255, 255), 1, 5, null), c => Assert.Equal((0, 0, 0), (c.R, c.G, c.B)));
    }
}
