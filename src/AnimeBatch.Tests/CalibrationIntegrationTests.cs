using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>Integração real da análise da Calibragem Automática: clipe sintético lavfi +
/// ffmpeg/ffprobe de tools\ — a encode de análise tem de rodar e produzir blocos cobrindo
/// a duração toda. Pula sozinho sem tools\.</summary>
public class CalibrationIntegrationTests
{
    [SkippableFact]
    public async Task AnalyzeAsync_produce_blocos_cobrindo_a_duracao()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();

        // Clipe sintético de 12s (2+ janelas de 5s) — trecho estático + trecho com movimento
        // alto (testsrc2) para gerar variação real de consumo entre janelas.
        var clip = Path.Combine(Path.GetTempPath(), $"animebatch-testclip-{Guid.NewGuid():N}.mkv");
        IntegrationHelpers.RunFfmpeg(
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:size=320x240:rate=24:duration=8",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24:duration=4",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1",
            "-an", "-c:v", "libx264", "-qp", "0", clip);

        try
        {
            var service = new CalibrationService(IntegrationHelpers.Tools.FfmpegPath!, IntegrationHelpers.Tools.FfprobePath!);
            var result = await service.AnalyzeAsync(clip, durationSeconds: 12);

            Assert.NotEmpty(result.Blocks);
            Assert.Equal(1, result.Blocks[0].Number);
            Assert.Equal(0, result.Blocks[0].StartSeconds);
            Assert.Equal(12, result.Blocks[^1].EndSeconds); // cobre o vídeo até o fim
            Assert.All(result.Blocks, b => Assert.True(b.EndSeconds > b.StartSeconds));
            Assert.Equal(1000, result.AnalysisKbps);
            Assert.True(result.MaxKbps > 0, "a análise precisa medir consumo não-zero");
        }
        finally
        {
            try { File.Delete(clip); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task AnalyzeAsync_cancelada_lanca_OperationCanceled()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();

        var clip = Path.Combine(Path.GetTempPath(), $"animebatch-testclip-{Guid.NewGuid():N}.mkv");
        // clipe LONGO o bastante para a encode de análise ainda estar rodando aos 500 ms
        IntegrationHelpers.RunFfmpeg(
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24:duration=600",
            "-an", "-c:v", "libx264", "-qp", "0", clip);
        try
        {
            var service = new CalibrationService(IntegrationHelpers.Tools.FfmpegPath!, IntegrationHelpers.Tools.FfprobePath!);
            using var cts = new CancellationTokenSource(500);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.AnalyzeAsync(clip, 600, ct: cts.Token));
            // o arquivo de probe temporário é apagado no finally do serviço — sem lixo
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "animebatch-calib-*.mkv"));
        }
        finally
        {
            try { File.Delete(clip); } catch { /* best-effort */ }
        }
    }
}
