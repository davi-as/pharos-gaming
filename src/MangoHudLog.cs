using System.Net.Sockets;

namespace PharosGaming;

/// <summary>
/// Liga o log do MangoHud com o overlay escondido. Com no_display, o MangoHud não roda o autostart_log; mas ele aceita
/// comandos por um socket (control=), e ":logging=1;" liga o log mesmo escondido. Mostrar/esconder (Ctrl+Shift+Q)
/// não interrompe o log; reler a config sim, e o socket só nasce de novo com outro nome (o MangoHud 0.8.4 não consegue
/// reabrir o mesmo): por isso cada arquivo que o PHAROS grava usa um nome novo, e aqui se lê o nome do arquivo em uso.
/// </summary>
public static class MangoHudLog
{
    private static readonly Dictionary<int, string> Started = new(); // pid → socket que já recebeu o comando

    /// <summary>Linha nova a cada gravação (o carimbo muda o nome do socket).</summary>
    public static string ControlLine() => $"control=mangohud-%p-{DateTime.UtcNow:HHmmssfff}";

    /// <summary>Nome do socket na config (a última linha control= vale, como no parser do MangoHud).</summary>
    public static string? ControlName(string conf, int pid) =>
        conf.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("control="))?[8..].Trim().Replace("%p", pid.ToString());

    /// <summary>Arquivo que o jogo usa: o por jogo do limitador, se existir; senão o MangoHud.conf (mesma ordem do MangoHud).</summary>
    public static string ConfFor(string key) => File.Exists(FrameLimiter.FileFor(key)) ? FrameLimiter.FileFor(key) : MangoHudConfig.FilePath;

    /// <summary>Chamado a cada poucos segundos fora da thread da interface.</summary>
    public static void Tick()
    {
        var games = FrameLimiter.Running().Where(g => g.Key != "wine-explorer").ToList();
        foreach (int dead in Started.Keys.Except(games.Select(g => g.Pid)).ToList()) Started.Remove(dead);
        foreach (var g in games)
        {
            FrameLimiter.Learn(AppSettings.Current, g);
            string? name = ControlName(Sys.Read(ConfFor(g.Key)) ?? "", g.Pid);
            if (name == null || Started.GetValueOrDefault(g.Pid) == name) continue;
            if (Send(name, ":logging=1;")) Started[g.Pid] = name;
        }
    }

    /// <summary>Socket abstrato do Linux ("\0nome"), o que o os_socket_listen_abstract do MangoHud cria.</summary>
    public static bool Send(string name, string command)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint("\0" + name));
            s.Send(System.Text.Encoding.ASCII.GetBytes(command));
            Thread.Sleep(200); // o MangoHud lê o socket uma vez por quadro: dá tempo antes de fechar
            return true;
        }
        catch (SocketException) { return false; } // jogo sem control= (ou reabrindo a config): tenta no próximo tick
    }
}
