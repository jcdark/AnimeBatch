using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Integração REAL do encode SVT-AV1 em 2-pass (bitrate média + Multipass): é o único
/// caminho do EncodeService que roda DUAS passadas com passlogfile — e que já quebrou
/// com "Invalid stats file size" quando a saída vinha antes dos -c:v na linha. Exercita
/// também o watchdog de stall (a passada 1 agora emite -progress como sinal de vida).
/// </summary>
public class EncodeSvtIntegrationTests
{
    [SkippableFact]
    public async Task Encode_svt_2pass_produz_arquivo_valido()
    {
        IntegrationHelpers.SkipIfNoFfmpeg();

        var work = Path.Combine(Path.GetTempPath(), "animebatch_svt_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var src = Path.Combine(work, "src.mkv");
            IntegrationHelpers.RunFfmpeg(
                $"-f lavfi -i testsrc2=size=640x360:rate=30:duration=2 -f lavfi -i sine=frequency=440:duration=2 " +
                $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -y \"{src}\"");

            var outPath = Path.Combine(work, "parte_01 - teste.mkv");
            var encode = new EncodeService(IntegrationHelpers.Tools.FfmpegPath!);
            var cfg = new CodecEncodeConfig { Code = "svt_av1", Preset = 6, UseConstantQuality = false, Multipass = true };
            var reports = new List<EncodeProgress>();

            await encode.EncodePartAsync(
                src, new EncodeService.JobItemRef(0, 2.0, 500), outPath,
                Path.Combine(work, "pass"), cfg, 500,
                CancellationToken.None, progress: new Progress<EncodeProgress>(reports.Add));

            Assert.True(File.Exists(outPath), "encode não produziu o arquivo");
            Assert.True(new FileInfo(outPath).Length > 1000, "arquivo de saída suspeito de vazio");
            Assert.True(reports.Count > 0, "nenhum progresso emitido (a 2ª passada também emite -progress)");

            var probe = new ProbeService(IntegrationHelpers.Tools.FfprobePath!);
            var info = await probe.ProbeAsync(outPath);
            Assert.Equal(2, info.DurationSeconds, 0); // corte -ss/-t respeitado
            Assert.NotEmpty(info.AudioStreams);       // AAC 160k re-encodado
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
