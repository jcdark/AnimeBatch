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
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", src);

            var outPath = Path.Combine(work, "parte_01 - teste.mkv");
            var encode = new EncodeService(IntegrationHelpers.Tools.FfmpegPath!, handBrakeCliPath: IntegrationHelpers.Tools.HandBrakeCliPath);
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

    [SkippableFact]
    public async Task Svt_10bit_com_perfil_professional_salvo_encoda_como_main()
    {
        // regressão REAL (18/09): config antiga com Profile="2" + SVT 4.x novo =
        // "Profile 2 bit-depth < 10 requires 4:2:2 color format" → ffmpeg sai -22 e o
        // job erro. O BuildVideoArgs precisa coagir para Main (0) e o encode tem que sair.
        IntegrationHelpers.SkipIfNoFfmpeg();

        var work = Path.Combine(Path.GetTempPath(), "animebatch_svt_prof_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var src = Path.Combine(work, "src.mkv");
            IntegrationHelpers.RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=2",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", src);

            var outPath = Path.Combine(work, "02 - Intro - teste.mkv");
            var encode = new EncodeService(IntegrationHelpers.Tools.FfmpegPath!, handBrakeCliPath: IntegrationHelpers.Tools.HandBrakeCliPath);
            var cfg = new CodecEncodeConfig
            {
                Code = "svt_av1_10bit", Preset = 6, Multipass = true, Profile = "2",
            };

            await encode.EncodePartAsync(
                src, new EncodeService.JobItemRef(0, 2.0, 500), outPath,
                Path.Combine(work, "pass"), cfg, 500,
                CancellationToken.None, progress: null);

            Assert.True(File.Exists(outPath), "encode não produziu o arquivo (perfil não coagido para Main?)");

            var probe = new ProbeService(IntegrationHelpers.Tools.FfprobePath!);
            var info = await probe.ProbeAsync(outPath);
            Assert.Equal(2, info.DurationSeconds, 0);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
