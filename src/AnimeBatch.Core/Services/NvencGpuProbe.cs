using System.Diagnostics;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Placas NVIDIA no espaço de índices do NVENC — que é a ordem CUDA, a mesma do
/// nvidia-smi. O "-gpu N" do av1_nvenc segue ESSE espaço; não confundir com Vulkan
/// (ncnn) ou DXGI/DirectML (ONNX), que podem numerar diferente.
/// </summary>
public static class NvencGpuProbe
{
    private static IReadOnlyList<(int Index, string Name)>? _cache;

    public static IReadOnlyList<(int Index, string Name)> Detect()
    {
        if (_cache is not null)
            return _cache;

        IReadOnlyList<(int Index, string Name)> devices = [];
        try
        {
            var smi = DmlDeviceCalibration.FindNvidiaSmi();
            if (smi is not null)
            {
                var (stdout, ok) = Run(smi, "--query-gpu=index,name --format=csv,noheader", 10_000);
                if (ok)
                    devices = ParseLines(stdout.Split('\n'));
            }
        }
        catch
        {
            // sem nvidia-smi (driver sem CLI, máquina sem NVIDIA) — fica vazio mesmo
        }
        _cache = devices;
        return _cache;
    }

    /// <summary>Linhas "0, NVIDIA GeForce RTX 4060 Ti" → dispositivos; linhas ruins são ignoradas.</summary>
    public static IReadOnlyList<(int Index, string Name)> ParseLines(IEnumerable<string?> lines)
    {
        var devices = new List<(int Index, string Name)>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var comma = raw.IndexOf(',');
            if (comma <= 0 || !int.TryParse(raw[..comma].Trim(), out var index) || index < 0)
                continue;
            var name = raw[(comma + 1)..].Trim();
            if (name.Length == 0)
                continue;
            devices.Add((index, name));
        }
        return devices;
    }

    /// <summary>Workers de encode a partir da configuração da tela Hardware (GPU), resolvidos
    /// no espaço NVENC. Sem configuração → null (encode sequencial); configuração com todas as
    /// placas em 0 workers → lista vazia (intenção explícita: nada roda nelas).</summary>
    public static IReadOnlyList<int>? ResolveWorkers(string? cardsJson) =>
        ResolveWorkers(cardsJson, Detect());

    public static IReadOnlyList<int>? ResolveWorkers(
        string? cardsJson, IReadOnlyList<(int Index, string Name)> devices)
    {
        var cards = HardwareGpuService.Parse(cardsJson);
        if (cards.Count == 0)
            return null;
        return GpuSelector.Finish(cards, HardwareGpuService.ExpandWorkers(cards, devices));
    }

    private static (string Stdout, bool Ok) Run(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return ("", false);
            }
            return (stdout, proc.ExitCode == 0);
        }
        catch
        {
            return ("", false);
        }
    }
}
