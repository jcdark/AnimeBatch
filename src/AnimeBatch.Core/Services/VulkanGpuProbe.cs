using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Descobre as GPUs Vulkan disponíveis perguntando ao próprio upscaler: qualquer execução
/// dele lista todos os dispositivos no stderr ("[0 NVIDIA GeForce RTX 5060 Ti] ...").
/// Elimina o chute de índice manual — os índices Vulkan NÃO batem com os CUDA do nvidia-smi.
/// Detectado uma vez por execução do app (cache estático).
/// </summary>
public static partial class VulkanGpuProbe
{
    private static IReadOnlyList<(int Index, string Name)>? _cached;

    public static IReadOnlyList<(int Index, string Name)> Detected => _cached ?? [];

    /// <summary>Índices Vulkan das placas NVIDIA detectadas (a Intel da CPU fica de fora);
    /// fallback [0] quando a detecção falha.</summary>
    public static IReadOnlyList<int> NvidiaIndices() =>
        _cached is { Count: > 0 } list
            ? list.Where(d => d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                  .Select(d => d.Index)
                  .ToList()
            : [0];

    [GeneratedRegex(@"^\[(\d+)\s+([^\]]+)\]", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    /// <summary>Roda o upscaler num PNG minúsculo e interpreta a listagem de dispositivos.</summary>
    public static async Task<IReadOnlyList<(int Index, string Name)>> EnsureDetectedAsync(
        string? upscalerExePath, CancellationToken ct)
    {
        if (_cached is not null)
            return _cached;
        if (upscalerExePath is null || !File.Exists(upscalerExePath))
            return _cached = [];

        var tmp = Path.Combine(Path.GetTempPath(), "animebatch_gpu_probe");
        try
        {
            Directory.CreateDirectory(tmp);
            var tinyPng = Path.Combine(tmp, "in.png");
            // PNG 1x1 embutido (não depende do ffmpeg)
            await File.WriteAllBytesAsync(tinyPng, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), ct)
                .ConfigureAwait(false);

            // args por ferramenta: realesrgan usa -n <modelo> e -m models; realcugan usa -n <denoise> e -m models-se
            var isCugan = Path.GetFileName(upscalerExePath).Contains("realcugan", StringComparison.OrdinalIgnoreCase);
            var args = new List<string>
            {
                "-i", tinyPng,
                "-o", Path.Combine(tmp, "out.png"),
                "-s", "2",
            };
            if (isCugan)
                args.AddRange(["-n", "1", "-m", UpscaleService.ModelDirPath(upscalerExePath, "realcugan")]);
            else
                args.AddRange(["-n", "realesr-animevideov3", "-m", UpscaleService.ModelDirPath(upscalerExePath, "realesrgan")]);
            args.AddRange(["-g", "0", "-f", "png"]);

            var stderr = await RunCapturingStderrAsync(upscalerExePath, [.. args], ct).ConfigureAwait(false);

            var devices = DeviceLine().Matches(stderr)
                .Select(m => (Index: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                              Name: m.Groups[2].Value.Trim()))
                .GroupBy(d => d.Index)
                .Select(g => g.First())
                .OrderBy(d => d.Index)
                .ToList();

            return _cached = devices;
        }
        catch
        {
            // probe é best-effort: sem detecção, cai no fallback [0]
            return _cached = [];
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task<string> RunCapturingStderrAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return await stderrTask.ConfigureAwait(false);
    }
}
