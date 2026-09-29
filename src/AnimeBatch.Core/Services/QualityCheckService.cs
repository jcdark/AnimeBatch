using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AnimeBatch.Core.Queueing;

namespace AnimeBatch.Core.Services;

/// <summary>
/// QC de qualidade opcional da fila: compara o trecho da ORIGEM (referência) com a parte
/// ENCODEADA (distorcida) via libvmaf do ffmpeg e devolve VMAF/SSIM/PSNR médios.
/// Falha de QC NUNCA derruba a fila — o QueueRunner trata qualquer exceção como "não medida".
/// Normalizações (mesmas do lab\tools\measure-vmaf.ps1): fps da parte para as duas entradas
/// (a origem pode ser VFR), escala bicúbica da referência para as dimensões da PARTE (com
/// upscale a parte é maior — a métrica isola o encode+upscale contra a origem) e corte no
/// menor frame count.
/// </summary>
public sealed class QualityCheckService : IQualityCheckStage
{
    /// <summary>Teto de segurança: QC não deve levar mais que isso (sem teto, um ffmpeg
    /// travado em arquivo estranho deixaria o slot da fila preso).</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(30);

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public QualityCheckService(string ffmpegPath, string ffprobePath)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    public async Task<QualityResult?> MeasureAsync(QualityCheckRequest req, CancellationToken ct)
    {
        var dist = await ProbeAsync(req.DistPath).ConfigureAwait(false);
        var @ref = await ProbeAsync(req.RefPath).ConfigureAwait(false);
        if (dist is null || @ref is null || dist.FpsFraction is null || dist.FpsFraction == "0/0")
            return null;

        var frames = Math.Min(@ref.Frames, dist.Frames);
        if (frames <= 0)
            return null;

        // O ':' de C:\ quebra o parser de opções do filtergraph (nem entre aspas) — o log do
        // libvmaf vai por CAMINHO RELATIVO com WorkingDirectory no temp (mesma saída do lab).
        var workDir = Path.Combine(Path.GetTempPath(), $"animebatch_vmaf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var graph =
                $"[0:v]fps={dist.FpsFraction},scale={dist.Width}:{dist.Height}:flags=bicubic,format=yuv420p,trim=end_frame={frames},setpts=PTS-STARTPTS[ref];" +
                $"[1:v]fps={dist.FpsFraction},format=yuv420p,trim=end_frame={frames},setpts=PTS-STARTPTS[dist];" +
                "[ref][dist]libvmaf=log_path=vmaf_log.json:log_fmt=json:n_threads=" +
                $"{Math.Max(2, Environment.ProcessorCount / 2)}:feature='name=psnr':feature='name=float_ssim'[out]";

            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", req.RefStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", req.RefDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", req.RefPath,
                "-i", req.DistPath,
                "-filter_complex", graph,
                "-map", "[out]",
                "-f", "null", "-",
            };

            using var proc = ProcessRunner.Start(_ffmpegPath, args, configure: psi => psi.WorkingDirectory = workDir);
            // Drena stdout/stderr (evita deadlock de pipe cheio); o resultado útil é o JSON.
            var drainOut = proc.StandardOutput.ReadToEndAsync();
            var drainErr = proc.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(HardTimeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ProcessRunner.TryKill(proc); // timeout interno da QC, não cancelamento da fila
                return null;
            }
            if (proc.ExitCode != 0)
                return null;

            return await ParsePooledAsync(Path.Combine(workDir, "vmaf_log.json")).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>Frames + fps fracionário + dimensões do primeiro stream de vídeo. Null = sem vídeo.</summary>
    private async Task<VmafInputInfo?> ProbeAsync(string file)
    {
        var (stdout, ok) = ProcessRunner.Capture(_ffprobePath,
            new[]
            {
                "-v", "error", "-select_streams", "v:0", "-count_frames",
                "-show_entries", "stream=avg_frame_rate,width,height,nb_read_frames",
                "-of", "default=noprint_wrappers=1", file,
            },
            timeoutMs: 120_000);
        if (!ok)
            return null;

        string? fpsFraction = null, width = null, height = null, frames = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            switch (line[..eq])
            {
                case "avg_frame_rate": fpsFraction = line[(eq + 1)..]; break;
                case "width": width = line[(eq + 1)..]; break;
                case "height": height = line[(eq + 1)..]; break;
                case "nb_read_frames": frames = line[(eq + 1)..]; break;
            }
        }

        if (fpsFraction is null || !long.TryParse(width, out var w) || !long.TryParse(height, out var h))
            return null;
        if (!long.TryParse(frames, out var frameCount) || frameCount <= 0)
            return null;
        return new VmafInputInfo(fpsFraction, w, h, frameCount);
    }

    /// <summary>Extrai as médias pooled do JSON do libvmaf. PSNR: nem toda build expõe
    /// "psnr" (algumas só psnr_y/psnr_hvs) — usa a primeira chave psnr*.</summary>
    private static async Task<QualityResult?> ParsePooledAsync(string logPath)
    {
        if (!File.Exists(logPath))
            return null;
        await using var stream = File.OpenRead(logPath);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("pooled_metrics", out var pooled))
            return null;

        var vmaf = PooledMean(pooled, "vmaf");
        if (vmaf is null)
            return null;
        var ssim = PooledMean(pooled, "float_ssim");
        double? psnr = null;
        foreach (var prop in pooled.EnumerateObject())
        {
            if (prop.Name.StartsWith("psnr", StringComparison.Ordinal))
            {
                psnr = PooledMean(pooled, prop.Name);
                break;
            }
        }
        return new QualityResult(vmaf.Value, ssim ?? 0, psnr ?? 0);
    }

    private static double? PooledMean(JsonElement pooled, string name) =>
        pooled.TryGetProperty(name, out var v) &&
        v.TryGetProperty("mean", out var mean) &&
        mean.TryGetDouble(out var d)
            ? d
            : null;

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* temp preso não pode derrubar a fila */ }
    }

    private sealed record VmafInputInfo(string FpsFraction, long Width, long Height, long Frames);
}
