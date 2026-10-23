using System.Diagnostics;
using System.Text;

namespace PharosGaming;

/// <summary>Leitura de arquivos do sistema e execução de comandos (comuns, ou como root via pkexec).</summary>
public static class Sys
{
    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    public static long? ReadLong(string path) => long.TryParse(Read(path), out long v) ? v : null;

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>~/.config (ou XDG_CONFIG_HOME): os testes apontam para uma pasta temporária e nunca tocam na config de verdade.</summary>
    public static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config");

    public static bool Has(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, command)));

    public sealed record Result(int Code, string Out, string Err)
    {
        public bool Ok => Code == 0;
        /// <summary>pkexec devolve 126 quando a senha é cancelada e 127 quando não autoriza.</summary>
        public bool Denied => Code is 126 or 127;
    }

    /// <summary>Roda um programa e captura a saída. Nunca lança: falha de execução vira código -1.</summary>
    public static async Task<Result> RunAsync(string file, IEnumerable<string> args, CancellationToken ct = default, string? stdin = null,
                                              IDictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["LC_ALL"] = "C"; // saída em inglês e com ponto decimal: é o que os parsers esperam
            if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            // ConfigureAwait(false) em tudo: quem chama de forma síncrona (GetResult) na thread da interface não trava
            if (stdin != null) { await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false); p.StandardInput.Close(); }
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            return new Result(p.ExitCode, await o.ConfigureAwait(false), await e.ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Result(-1, "", ex.Message); }
    }

    public static Task<Result> RunAsync(string file, params string[] args) => RunAsync(file, (IEnumerable<string>)args);

    /// <summary>
    /// Roda um script de shell como root com uma única senha (pkexec). O script vai pela entrada padrão, nunca pela linha de
    /// comando, e os valores variáveis devem passar por <see cref="Quote"/>.
    /// </summary>
    public static Task<Result> RunRootAsync(string script, CancellationToken ct = default) =>
        RunAsync("pkexec", new[] { "/bin/sh", "-s" }, ct, script);

    /// <summary>Aspas simples de shell: seguro para qualquer texto.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string Bytes(double b, bool si = false)
    {
        double k = si ? 1000 : 1024;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= k && i < u.Length - 1) { b /= k; i++; }
        return $"{b:0.#} {u[i]}";
    }

    public static string RootError(Result r) =>
        r.Denied ? "Senha cancelada ou sem permissão." : (r.Err.Trim().Length > 0 ? r.Err.Trim() : r.Out.Trim()) is { Length: > 0 } m ? m : $"código {r.Code}";

    public static readonly Encoding Utf8 = new UTF8Encoding(false);
}
