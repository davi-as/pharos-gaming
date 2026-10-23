using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PharosGaming;

/// <summary>Histórico dos benchmarks e a trava que impede dois testes pesados ao mesmo tempo.</summary>
public static class BenchLog
{
    /// <summary>Nome do teste em andamento (benchmark ou stress); null = livre.</summary>
    public static string? Busy { get; set; }

    public static void Save(string test, double score, string summary)
    {
        var s = AppSettings.Current;
        s.BenchRecords.Insert(0, new BenchRecord(DateTime.Now, test, score, summary));
        // Mantém os 10 mais recentes de cada teste.
        s.BenchRecords = s.BenchRecords.GroupBy(r => r.Test).SelectMany(g => g.Take(10)).OrderByDescending(r => r.Date).ToList();
        try { s.Save(); } catch { }
    }

    /// <summary>Barras do histórico: cada uma comparada com o melhor resultado do mesmo teste (só isso é comparável).</summary>
    public static List<MeterItem> History(Func<string, bool> test)
    {
        var list = AppSettings.Current.BenchRecords.Where(r => test(r.Test)).OrderByDescending(r => r.Date).ToList();
        var best = list.GroupBy(r => r.Test).ToDictionary(g => g.Key, g => g.Max(r => r.Score));
        return list.Select(r => new MeterItem($"{r.Date:dd/MM HH:mm}", r.Summary, best[r.Test] > 0 ? r.Score / best[r.Test] : 0)).ToList();
    }

    public static string TestName(string t) => t.Split(':')[0] switch
    {
        "gpu" => "Cena de jogo (GPU)", "rt" => "Ray tracing (GPU)", "sim" => "Simulação de jogo (CPU)", "mem" => "Memória e carregamento",
        "disk" => "Disco", "oc" => "PHAROS OC (estabilidade)", _ => t
    };
}

/// <summary>Cópia das imagens dos testes para bitmaps da interface.</summary>
public static class PreviewBitmap
{
    public static WriteableBitmap New(int w, int h) =>
        new(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    /// <summary>Prévia do OpenGL (RGBA, de baixo para cima) → bitmap (BGRA, de cima para baixo).</summary>
    public static unsafe void FromGl(GlPreview p, WriteableBitmap bmp)
    {
        using var fb = bmp.Lock();
        lock (p.Pixels)
            fixed (byte* src = p.Pixels)
                for (int y = 0; y < GlPreview.H; y++)
                {
                    byte* s = src + (GlPreview.H - 1 - y) * GlPreview.W * 4;
                    byte* d = (byte*)fb.Address + y * fb.RowBytes;
                    for (int x = 0; x < GlPreview.W; x++, s += 4, d += 4) { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = 255; }
                }
    }

    public static unsafe void FromBgra(int[] pixels, int w, int h, WriteableBitmap bmp)
    {
        using var fb = bmp.Lock();
        fixed (int* src = pixels)
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + y * w, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, w * 4);
    }
}
