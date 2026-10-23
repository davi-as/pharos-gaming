using System.Net.Sockets;
using System.Text;

namespace PharosGaming;

/// <summary>Dispositivo de iluminação do OpenRGB.</summary>
public sealed class LightDevice
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Posição horizontal (0–1) de cada LED; o tamanho do vetor é a quantidade de LEDs.</summary>
    public double[] LedX { get; init; } = Array.Empty<double>();
    public int LedCount => LedX.Length;
    public bool Selected { get; set; } = true;
    internal int ActiveMode { get; init; }
    internal byte[]? ModeBytes { get; init; }
}

/// <summary>
/// Cliente do servidor SDK do OpenRGB (localhost:6742). No Linux ele cobre praticamente tudo: placa-mãe (MSI Mystic Light,
/// Gigabyte RGB Fusion, ASUS Aura), RAM, GPU, Corsair, Razer (via OpenRazer), Logitech... Protocolo binário little-endian;
/// negociamos a versão 3.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _ns;
    public int Protocol { get; private set; } = 3;

    private const uint ReqControllerCount = 0, ReqControllerData = 1, ReqProtocolVersion = 40, SetClientName = 50,
                       UpdateMode = 1101, UpdateLeds = 1050, SetCustomMode = 1100;

    public static byte[] Packet(uint dev, uint type, byte[] payload)
    {
        var p = new byte[16 + payload.Length];
        "ORGB"u8.CopyTo(p);
        BitConverter.TryWriteBytes(p.AsSpan(4), dev);
        BitConverter.TryWriteBytes(p.AsSpan(8), type);
        BitConverter.TryWriteBytes(p.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(p, 16);
        return p;
    }

    private void Write(uint dev, uint type, byte[] payload)
    {
        _ns!.Write(Packet(dev, type, payload));
        _ns.Flush();
    }

    private byte[] ReadExact(int n)
    {
        var b = new byte[n];
        int o = 0;
        while (o < n)
        {
            int r = _ns!.Read(b, o, n - o);
            if (r <= 0) throw new IOException("conexão encerrada pelo OpenRGB");
            o += r;
        }
        return b;
    }

    /// <summary>Lê pacotes até chegar o do tipo pedido (ignora avisos assíncronos do servidor).</summary>
    private byte[] ReadType(uint wanted)
    {
        for (int i = 0; i < 20; i++)
        {
            var h = ReadExact(16);
            if (h[0] != 'O' || h[1] != 'R' || h[2] != 'G' || h[3] != 'B') throw new IOException("resposta inválida (não é o protocolo do OpenRGB)");
            uint type = BitConverter.ToUInt32(h, 8);
            int size = (int)BitConverter.ToUInt32(h, 12);
            if (size < 0 || size > 4_000_000) throw new IOException("pacote grande demais");
            var data = size > 0 ? ReadExact(size) : Array.Empty<byte>();
            if (type == wanted) return data;
        }
        throw new IOException("o OpenRGB não respondeu como esperado");
    }

    private static string Str(BinaryReader r)
    {
        int len = r.ReadUInt16();
        var b = r.ReadBytes(len);
        int n = b.Length;
        while (n > 0 && b[n - 1] == 0) n--;
        return Encoding.UTF8.GetString(b, 0, n);
    }

    /// <summary>Dados de um controlador (protocolo 3): nome, fabricante, LEDs e o modo ativo (para devolver no fim).</summary>
    public static (string Name, string Vendor, int Leds, int Active, byte[]? ModeBytes) ParseController(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32(); // tamanho
        r.ReadInt32();  // tipo do dispositivo
        string name = Str(r);
        string vendor = Str(r);
        Str(r);                       // descrição
        Str(r); Str(r); Str(r);       // versão, serial, local
        int numModes = r.ReadUInt16();
        int active = r.ReadInt32();
        byte[]? activeBytes = null;
        for (int m = 0; m < numModes; m++)
        {
            int start = (int)ms.Position;
            Str(r); r.ReadInt32(); r.ReadUInt32();                    // nome, valor, flags
            r.ReadUInt32(); r.ReadUInt32();                           // velocidade mín/máx
            r.ReadUInt32(); r.ReadUInt32();                           // brilho mín/máx
            r.ReadUInt32(); r.ReadUInt32();                           // cores mín/máx
            r.ReadUInt32(); r.ReadUInt32();                           // velocidade, brilho
            r.ReadUInt32(); r.ReadUInt32();                           // direção, modo de cor
            int nc = r.ReadUInt16();
            r.ReadBytes(nc * 4);
            if (m == active) activeBytes = data[start..(int)ms.Position];
        }
        int numZones = r.ReadUInt16();
        for (int z = 0; z < numZones; z++)
        {
            Str(r); r.ReadInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            int matrixLen = r.ReadUInt16();
            if (matrixLen > 0) r.ReadBytes(matrixLen);
        }
        int numLeds = r.ReadUInt16();
        return (name, vendor, numLeds, active, activeBytes);
    }

    public (List<LightDevice> Devices, string Status) Connect(string host = "127.0.0.1", int port = 6742)
    {
        Dispose();
        var list = new List<LightDevice>();
        try
        {
            var tcp = new TcpClient { NoDelay = true };
            if (!tcp.ConnectAsync(host, port).Wait(2000)) { tcp.Dispose(); return (list, "o OpenRGB não respondeu na porta " + port + "."); }
            _tcp = tcp;
            _ns = tcp.GetStream();
            _ns.ReadTimeout = 3000;
            _ns.WriteTimeout = 3000;
            Write(0, SetClientName, Encoding.ASCII.GetBytes("PHAROS GAMING\0"));
            Write(0, ReqProtocolVersion, BitConverter.GetBytes(3u));
            uint server;
            try { server = BitConverter.ToUInt32(ReadType(ReqProtocolVersion), 0); }
            catch (IOException) { Dispose(); return (list, "o OpenRGB é antigo demais (sem negociação de protocolo). Atualize para a versão 0.9 ou mais nova."); }
            Protocol = (int)Math.Min(3u, server);
            if (Protocol < 3) { Dispose(); return (list, $"protocolo {Protocol} do OpenRGB é antigo. Atualize para a versão 0.9 ou mais nova."); }
            Write(0, ReqControllerCount, Array.Empty<byte>());
            int count = (int)BitConverter.ToUInt32(ReadType(ReqControllerCount), 0);
            for (int i = 0; i < count; i++)
            {
                Write((uint)i, ReqControllerData, BitConverter.GetBytes((uint)Protocol));
                var (name, vendor, leds, active, modeBytes) = ParseController(ReadType(ReqControllerData));
                if (leds <= 0) continue;
                var x = new double[leds];
                for (int k = 0; k < leds; k++) x[k] = leds > 1 ? k / (double)(leds - 1) : 0;
                list.Add(new LightDevice
                {
                    Index = i, Name = name, Detail = $"{(string.IsNullOrWhiteSpace(vendor) ? "—" : vendor)} · {leds} LEDs",
                    LedX = x, ActiveMode = active, ModeBytes = modeBytes,
                });
            }
            return (list, $"conectado (protocolo {Protocol}) · {list.Count} dispositivo(s) com LEDs de {count}.");
        }
        catch (Exception ex)
        {
            Dispose();
            bool refused = ex is AggregateException { InnerException: SocketException } || ex is SocketException;
            return (list, refused ? $"o servidor SDK do OpenRGB não está ligado em {host}:{port}." : "falha na conversa com o OpenRGB: " + ex.Message);
        }
    }

    /// <summary>Põe o dispositivo no modo "Direct/Custom", em que as cores vêm do cliente.</summary>
    public bool Acquire(LightDevice d)
    {
        try { Write((uint)d.Index, SetCustomMode, Array.Empty<byte>()); return true; }
        catch { return false; }
    }

    public static byte[] LedsPayload(ReadOnlySpan<(byte R, byte G, byte B)> colors, int ledCount)
    {
        int n = Math.Min(colors.Length, ledCount);
        var p = new byte[6 + 4 * n];
        BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
        BitConverter.TryWriteBytes(p.AsSpan(4), (ushort)n);
        for (int i = 0; i < n; i++) { p[6 + i * 4] = colors[i].R; p[7 + i * 4] = colors[i].G; p[8 + i * 4] = colors[i].B; }
        return p;
    }

    public bool Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        try { Write((uint)d.Index, UpdateLeds, LedsPayload(colors, d.LedCount)); return true; }
        catch { return false; }
    }

    /// <summary>Volta o dispositivo ao modo que estava ativo quando conectamos.</summary>
    public void Release(LightDevice d)
    {
        if (d.ModeBytes == null) return;
        try
        {
            var p = new byte[8 + d.ModeBytes.Length];
            BitConverter.TryWriteBytes(p.AsSpan(0), (uint)p.Length);
            BitConverter.TryWriteBytes(p.AsSpan(4), (uint)d.ActiveMode);
            d.ModeBytes.CopyTo(p, 8);
            Write((uint)d.Index, UpdateMode, p);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _ns?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _ns = null; _tcp = null;
    }
}

/// <summary>Efeitos de iluminação: a cor de cada LED num instante.</summary>
public static class LightEffects
{
    public static (byte R, byte G, byte B) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Cor de um valor ao vivo: temperatura (35 °C azul → 85 °C vermelho) ou carga (0% verde → 100% vermelho).</summary>
    public static (byte, byte, byte) LiveColor(string effect, float value) => effect == "load"
        ? Hsv(120 * (1 - Math.Clamp(value / 100.0, 0, 1)), 1, 1)
        : Hsv(240 * (1 - Math.Clamp((value - 35) / 50.0, 0, 1)), 1, 1);

    /// <summary>Cores de todos os LEDs de um dispositivo. <paramref name="live"/> é a cor dos efeitos por sensor.</summary>
    public static (byte R, byte G, byte B)[] Frame(string effect, double t, double[] ledX, (byte R, byte G, byte B) color, double bright, double speed,
                                                   (byte R, byte G, byte B)? live)
    {
        var cols = new (byte, byte, byte)[ledX.Length];
        var (cr, cg, cb) = color;
        for (int i = 0; i < cols.Length; i++)
        {
            (byte R, byte G, byte B) c = effect switch
            {
                "breath" => Scale(color, 0.04 + 0.96 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (0.1 * speed) * t))),
                "rainbow" => Hsv(ledX[i] * 360 + t * speed * 36, 1, 1),
                "cpu" or "gpu" or "load" => live ?? (0, 0, 40),
                "off" => (0, 0, 0),
                _ => (cr, cg, cb),
            };
            cols[i] = Scale(c, bright);
        }
        return cols;
    }

    private static (byte, byte, byte) Scale((byte R, byte G, byte B) c, double k) => ((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
}
