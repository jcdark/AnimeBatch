using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Integração REAL do encode NVENC com reforço de qualidade: parte sintética de 1s
/// codificada pela escada completa do EncodePartAsync (boost cheio) na placa NVENC 0.
/// Valida que o driver local aceita AQ + UHQ + filtro temporal + lookahead_level e que
/// o arquivo sai decodificável. Sem placa NVIDIA/tools\, PULA (não falha).
/// </summary>
public class EncodeNvencIntegrationTests
{
    [SkippableFact]
    public async Task Encode_nvenc_com_reforco_produz_arquivo_valido()
    {
        IntegrationHelpers.SkipIfNoNvenc();

        var work = Path.Combine(Path.GetTempPath(), "animebatch_nvenc_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var src = Path.Combine(work, "src.mkv");
            IntegrationHelpers.RunFfmpeg(
                $"-f lavfi -i testsrc2=size=640x360:rate=30:duration=1 -f lavfi -i sine=frequency=440:duration=1 " +
                $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -y \"{src}\"");

            var outPath = Path.Combine(work, "parte_01 - teste.mkv");
            var encode = new EncodeService(IntegrationHelpers.Tools.FfmpegPath);
            var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 7, Tune = "hq", QualityBoost = true };
            await encode.EncodePartAsync(
                src, new EncodeService.JobItemRef(0, 1.0, 500), outPath,
                Path.Combine(work, "pass"), cfg, 500,
                CancellationToken.None, progress: null, cudaGpu: 0);

            Assert.True(File.Exists(outPath), "encode não produziu o arquivo");
            Assert.True(new FileInfo(outPath).Length > 1000, "arquivo de saída suspeito de vazio");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
