using AnimeBatch.Core.Queueing;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Testes de integração da QC de qualidade (libvmaf real do ffmpeg de tools\): vídeos
/// sintéticos lavfi, nada do acervo. Pula sem tools\ (mesma técnica das demais suítes).
/// </summary>
public class QualityCheckServiceIntegrationTests
{
    private static QualityCheckService NewService() =>
        new(IntegrationHelpers.Tools.FfmpegPath!, IntegrationHelpers.Tools.FfprobePath!);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"animebatch-qc-{Guid.NewGuid():N}");

    [Fact]
    public async Task Mede_vmaf_ssim_de_um_reencode_similar()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.mp4");
            var dist = Path.Combine(dir, "dist.mp4");
            IntegrationHelpers.RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=duration=2:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", src);
            // re-encode próximo: qualidade alta esperada
            IntegrationHelpers.RunFfmpeg(
                "-i", src, "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", dist);

            var result = await NewService().MeasureAsync(
                new QualityCheckRequest(src, 0, 2.0, dist), CancellationToken.None);

            Assert.NotNull(result);
            Assert.InRange(result!.Vmaf, 80, 100);
            Assert.InRange(result.Ssim, 0.9, 1.0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Parte_maior_que_a_origem_upscale_e_escala_a_referencia()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.mp4");
            var dist = Path.Combine(dir, "dist_big.mp4");
            IntegrationHelpers.RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=duration=2:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", src);
            // distorcida AMPLIADA (caminho do upscale do app): a referência é quem sobe de tamanho
            IntegrationHelpers.RunFfmpeg(
                "-i", src, "-vf", "scale=640:480:flags=bicubic",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", dist);

            var result = await NewService().MeasureAsync(
                new QualityCheckRequest(src, 0, 2.0, dist), CancellationToken.None);

            Assert.NotNull(result);
            Assert.InRange(result!.Vmaf, 70, 100);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Trecho_da_origem_com_offset_medida_por_start_e_duration()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.mp4");
            var dist = Path.Combine(dir, "dist_tail.mp4");
            IntegrationHelpers.RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=duration=4:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", src);
            // distorcida = segundo 1 em diante (como uma parte que começa em 1.0s)
            IntegrationHelpers.RunFfmpeg(
                "-ss", "1", "-i", src,
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", dist);

            var result = await NewService().MeasureAsync(
                new QualityCheckRequest(src, 1.0, 3.0, dist), CancellationToken.None);

            Assert.NotNull(result);
            Assert.InRange(result!.Vmaf, 70, 100);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Arquivo_de_distorcao_invalido_devolve_null_sem_lancar()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.mp4");
            var dist = Path.Combine(dir, "nao-existe.mp4");
            IntegrationHelpers.RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=duration=1:size=320x240:rate=30",
                "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "18", "-y", src);

            var result = await NewService().MeasureAsync(
                new QualityCheckRequest(src, 0, 1.0, dist), CancellationToken.None);

            Assert.Null(result); // QC é best-effort: falha = null, nunca exceção
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
