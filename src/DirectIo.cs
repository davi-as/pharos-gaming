using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PharosGaming;

/// <summary>
/// Arquivos com O_DIRECT (sem o cache de páginas do kernel), para os benchmarks medirem o disco e não a RAM.
/// O .NET não traduz FileOptions.NoBuffering no Linux, então o open é feito direto na libc.
/// </summary>
public static class DirectIo
{
    private const int O_RDONLY = 0, O_RDWR = 2, O_CREAT = 0x40, O_TRUNC = 0x200, O_DSYNC = 0x1000, O_DIRECT = 0x4000;

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, int mode);

    /// <summary>Abre com O_DIRECT; se o sistema de arquivos não aceitar (tmpfs, alguns FUSE), lança IOException explicando.</summary>
    public static SafeFileHandle Open(string path, bool write, bool create = false)
    {
        int flags = (write ? O_RDWR | O_DSYNC : O_RDONLY) | O_DIRECT | (create ? O_CREAT | O_TRUNC : 0);
        int fd = open(path, flags, 0x180); // 0600
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new IOException(errno == 22
                ? "Este sistema de arquivos não aceita acesso direto ao disco (O_DIRECT). Escolha outra unidade."
                : $"Não consegui abrir {path} (errno {errno}).");
        }
        return new SafeFileHandle(fd, ownsHandle: true);
    }
}
