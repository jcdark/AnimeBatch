using System.Diagnostics;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Utilidades de processo compartilhadas (antes duplicadas em EncodeService, MergeService,
/// UpscaleService, NvencGpuProbe e DmlDeviceCalibration): start com pipes redirecionados,
/// captura síncrona com timeout, kill de árvore e truncamento de stderr para mensagens de erro.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Inicia um processo com stdout/stderr redirecionados (stdin opcional). O
    /// <paramref name="configure"/> roda ANTES do Start — é onde ajustar codificação de pipe
    /// (ffprobe devolve UTF-8; o default do pipe seria o encoding do console). O chamador é
    /// dono do Process (dispose/kill dele).</summary>
    public static Process Start(string exe, IEnumerable<string>? args = null,
        bool redirectStdin = false, Action<ProcessStartInfo>? configure = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            CreateNoWindow = true,
        };
        if (args is not null)
        {
            foreach (var a in args)
                psi.ArgumentList.Add(a);
        }
        configure?.Invoke(psi);

        var proc = new Process { StartInfo = psi };
        proc.Start();
        return proc;
    }

    /// <summary>Roda um utilitário síncrono com timeout rígido (nvidia-smi etc.) e devolve a
    /// stdout. Args DISCRETOS (um token por item) — vão pelo Start/ArgumentList, sem linha de
    /// comando única: nome de arquivo com espaço/aspas não precisa de quoting. NUNCA lança:
    /// qualquer falha (exe ausente, timeout, código != 0) devolve ("", false).</summary>
    public static (string Stdout, bool Ok) Capture(string exe, IReadOnlyList<string>? args, int timeoutMs)
    {
        try
        {
            using var proc = Start(exe, args);
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                TryKill(proc);
                return ("", false);
            }
            return (stdout, proc.ExitCode == 0);
        }
        catch
        {
            return ("", false);
        }
    }

    /// <summary>Variante do Capture que devolve também o stderr (diagnóstico de falha em
    /// testes). Mesmas garantias: nunca lança, timeout rígido com kill de árvore. O stderr é
    /// drenado EM PARALELO ao stdout — leitura sequencial deadlocka quando o filho enche o
    /// buffer do pipe (o banner do ffmpeg já passa de 4 KB) sem nunca fechar o stdout.</summary>
    public static (string Stdout, string Stderr, bool Ok) CaptureWithStderr(
        string exe, IReadOnlyList<string>? args, int timeoutMs)
    {
        try
        {
            using var proc = Start(exe, args);
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!proc.WaitForExit(timeoutMs))
            {
                TryKill(proc);
                return ("", "", false);
            }
            return (stdout, stderr, proc.ExitCode == 0);
        }
        catch
        {
            return ("", "", false);
        }
    }

    public static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // processo já morreu — nada a fazer
        }
    }

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
