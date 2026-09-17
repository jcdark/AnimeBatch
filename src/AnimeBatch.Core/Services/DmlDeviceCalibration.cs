using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Mapeia device ids DirectML → NOME da placa física quando a factory DXGI está
/// indisponível (builds Insider onde CreateDXGIFactory1 devolve E_NOINTERFACE e não há
/// nomes para casar com a configuração da tela Hardware (GPU)).
///
/// Técnica: o nvidia-smi reporta memória de VRAM por placa; uma inferência GRANDE (1024²)
/// numa sessão DML faz o BFC allocator reservar centenas de MB — a placa cujo
/// memory.used subiu é a dona do device id. Roda UMA vez por aplicativo (cache estático).
/// O índice do nvidia-smi (ordem CUDA) NUNCA é usado como id — só o nome.
/// </summary>
public static class DmlDeviceCalibration
{
    private const long MinDeltaMiB = 64; // delta de VRAM abaixo disso é ruído do desktop

    private static IReadOnlyList<(int DeviceId, string Name)>? _cached;

    public static IReadOnlyList<(int DeviceId, string Name)> ResolveDeviceNames(string onnxModelsDir)
    {
        if (_cached is not null)
            return _cached;

        try
        {
            var smi = FindNvidiaSmi();
            if (smi is null)
                return _cached = [];

            var modelFile = OnnxUpscaleService.PickModelFile(onnxModelsDir, 480);
            var before = QueryGpuMemory(smi);
            if (before.Count == 0)
                return _cached = [];

            var result = new List<(int DeviceId, string Name)>();
            foreach (var deviceId in OnnxUpscaleService.ProbeDeviceIds(modelFile))
            {
                var session = OnnxUpscaleService.GetSession(modelFile, deviceId, 70 + deviceId);
                RunBigInference(session, session.InputMetadata.Keys.First());

                var after = QueryGpuMemory(smi);
                var (name, delta) = BiggestRiser(before, after);
                if (delta >= MinDeltaMiB)
                    result.Add((deviceId, name));
                before = after; // próximo device parte do novo patamar de VRAM
            }

            return _cached = result;
        }
        catch
        {
            return _cached = []; // calibração é best-effort: sem ela, auto-modo por benchmark
        }
    }

    /// <summary>Placa NVIDIA com o MAIOR aumento de VRAM entre as duas leituras.</summary>
    private static (string Name, long DeltaMiB) BiggestRiser(
        IReadOnlyList<GpuMemory> before, IReadOnlyList<GpuMemory> after)
    {
        var best = ("", 0L);
        foreach (var gpu in before)
        {
            var now = after.FirstOrDefault(a => a.Index == gpu.Index);
            var delta = now is null ? 0 : now.MemMiB - gpu.MemMiB;
            if (delta > best.Item2)
                best = (gpu.Name, delta);
        }
        return best;
    }

    /// <summary>Inferência que força reserva de VRAM visível no nvidia-smi (BFC retém a arena).</summary>
    private static void RunBigInference(InferenceSession session, string inputName)
    {
        const int size = 1024;
        var input = new float[3L * size * size];
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, size, size });
        var inputs = new Dictionary<string, OrtValue> { [inputName] = tensor };
        using var runOpts = new RunOptions();
        using var outputs = session.Run(runOpts, inputs, session.OutputNames);
    }

    private record GpuMemory(int Index, string Name, long MemMiB);

    private static IReadOnlyList<GpuMemory> QueryGpuMemory(string smiExe)
    {
        var (stdout, ok) = Run(smiExe,
            "--query-gpu=index,name,memory.used --format=csv,noheader,nounits", 10_000);
        if (!ok)
            return [];

        var list = new List<GpuMemory>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "0, NVIDIA GeForce RTX 5060 Ti, 845" — o nome não tem vírgula na prática
            var parts = line.Split(", ");
            if (parts.Length < 3)
                continue;
            if (!int.TryParse(parts[0], out var index) ||
                !long.TryParse(parts[^1], out var mem))
                continue;
            list.Add(new GpuMemory(index, string.Join(", ", parts[1..^1]), mem));
        }
        return list;
    }

    /// <summary>Caminho do nvidia-smi.exe (System32 primeiro, depois PATH); null se não achar.
    /// Público porque o NvencGpuProbe usa o mesmo binário.</summary>
    public static string? FindNvidiaSmi()
    {
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(system32))
            return system32;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "nvidia-smi.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // segmento de PATH inválido — ignora
            }
        }
        return null;
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
