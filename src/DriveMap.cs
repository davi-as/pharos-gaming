namespace PharosGaming;

/// <summary>Cartão de um sistema de arquivos montado, com o anel de ocupação.</summary>
public sealed class DriveCard
{
    public string Mount { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public string Disk { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public bool Removable { get; init; }

    public double UsedFraction => Total > 0 ? (double)(Total - Free) / Total : 0;
    public string UsedPercent => $"{UsedFraction:P0}";
    /// <summary>Ângulo do arco usado do anel (0 a 360°).</summary>
    public double Sweep => Math.Clamp(UsedFraction, 0, 0.9999) * 360;
    public string Title => string.IsNullOrWhiteSpace(Label) ? Mount : $"{Mount}  {Label}";
    public string Sizes => $"{Sys.Bytes(Total - Free)} usados · {Sys.Bytes(Free)} livres de {Sys.Bytes(Total)}";
    public string Meta => string.Join(" · ", new[] { Kind, FileSystem, Disk }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>Sistemas de arquivos montados (pelo lsblk) e o disco físico de cada um.</summary>
public static class DriveMap
{
    public static List<DriveCard> Read() => FromDevices(SystemInfo.ReadBlockDevices(), mp =>
    {
        try { var d = new DriveInfo(mp); return (d.TotalSize, d.AvailableFreeSpace); }
        catch { return null; }
    });

    /// <summary>
    /// Uma entrada por sistema de arquivos (o btrfs monta o mesmo volume em vários lugares: fica o ponto mais curto, como "/").
    /// O tamanho vem do statvfs do ponto de montagem.
    /// </summary>
    public static List<DriveCard> FromDevices(List<BlockDev> roots, Func<string, (long Total, long Free)?> space)
    {
        var list = new List<DriveCard>();
        void Walk(BlockDev d, BlockDev disk)
        {
            var mounts = d.Mounts.Where(m => m.StartsWith('/')).OrderBy(m => m.Length).ToList();
            if (mounts.Count > 0 && space(mounts[0]) is var (total, free) && total > 0)
                list.Add(new DriveCard
                {
                    Mount = mounts[0], Label = d.Label ?? "", FileSystem = d.FsType ?? "", Disk = disk.Model ?? disk.Name,
                    Kind = disk.Tran == "usb" || disk.Removable ? "Externo (USB)" : SystemInfo.Kind(disk),
                    Total = total, Free = free, Removable = disk.Removable || disk.Tran == "usb",
                });
            foreach (var c in d.Children) Walk(c, disk);
        }
        foreach (var r in roots.Where(r => !r.Name.StartsWith("zram") && !r.Name.StartsWith("loop"))) Walk(r, r);
        return list.OrderBy(c => c.Removable).ThenBy(c => c.Mount).ToList();
    }
}
