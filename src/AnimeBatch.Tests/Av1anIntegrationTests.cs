using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Integração REAL do caminho Av1an: exige av1an.exe + SvtAv1EncApp em tools\ E o
/// VapourSynth/Python na máquina (DLLs de runtime); sem qualquer um deles os testes
/// PULAM. Exercita o fluxo completo do EncodeService: pré-corte lossless (o av1an rust
/// não tem --trim), fatiamento por cena do av1an e remux com áudio.
/// </summary>
public class Av1anIntegrationTests
{
    private static (string EnvPath, bool HasVs) Av1anEnv()
    {
        var dirs = new List<string>();
        var hasVs = false;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pyRoot = Path.Combine(local, "Programs", "Python");
        if (Directory.Exists(pyRoot))
        {
            foreach (var py in Directory.GetDirectories(pyRoot, "Python3*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var vs = Path.Combine(py, "Lib", "site-packages", "vapoursynth");
                if (File.Exists(Path.Combine(vs, "vsscript.dll")))
                {
                    dirs.Add(vs);
                    hasVs = true;
                }
                var scripts = Path.Combine(py, "Scripts");
                if (File.Exists(Path.Combine(scripts, "vspipe.exe")))
                    dirs.Add(scripts);
            }
        }
        if (IntegrationHelpers.Tools.ToolsDir is { } tools)
            dirs.Add(tools);
        return (string.Join(";", dirs), hasVs);
    }

    private static void SkipIfNoAv1an()
    {
        Skip.IfNot(File.Exists(IntegrationHelpers.Tools.Av1anPath ?? ""), "av1an.exe ausente em tools\\");
        Skip.IfNot(File.Exists(IntegrationHelpers.Tools.SvtAv1EncAppPath ?? ""), "SvtAv1EncApp.exe ausente em tools\\");
        var (_, hasVs) = Av1anEnv();
        Skip.IfNot(hasVs, "VapourSynth não instalado nesta máquina (vsscript.dll não encontrado)");
    }

    private static EncodeService NewService(int? tailTickMs = null)
    {
        var (envPath, _) = Av1anEnv();
        return new EncodeService(
            IntegrationHelpers.Tools.FfmpegPath!,
            handBrakeCliPath: IntegrationHelpers.Tools.HandBrakeCliPath,
            av1anPath: IntegrationHelpers.Tools.Av1anPath,
            svtAv1EncAppPath: IntegrationHelpers.Tools.SvtAv1EncAppPath,
            av1anEnvPath: envPath,
            ffprobePath: IntegrationHelpers.Tools.FfprobePath,
            av1anTailTickMs: tailTickMs);
    }

    [SkippableFact]
    public async Task Av1an_codifica_parte_com_corte_e_audio_sincronizado()
    {
        SkipIfNoAv1an();
        var work = Path.Combine(Path.GetTempPath(), "animebatch_av1an_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // origem 20s; a parte é 4s→14s — força o pré-corte lossless (sem --trim no av1an)
            var src = Path.Combine(work, "src.mkv");
            IntegrationHelpers.RunFfmpeg(
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24000/1001:duration=20",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=20",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", src);

            var outPath = Path.Combine(work, "parte.mkv");
            var reports = new List<EncodeProgress>();
            var progress = new Progress<EncodeProgress>(reports.Add);
            await NewService(tailTickMs: 50).EncodePartAsync(
                src, new EncodeService.JobItemRef(4, 14, 450), outPath, Path.Combine(work, "pass_01"),
                new CodecEncodeConfig { Code = "av1an_av1", Preset = 8, UseConstantQuality = true, Cq = 32 },
                450, CancellationToken.None, progress);

            Assert.True(File.Exists(outPath), "arquivo de saída não foi gerado");
            // Progress<T> posta os callbacks de forma assíncrona — dá tempo de chegarem
            await Task.Delay(500);
            Assert.True(reports.Count > 0, "nenhum relato de progresso veio do logfile");
            // o rodapé precisa de fps E velocidade vivos (bug 22/09: 2 workers com FPS 0.0
            // e Speed 0.0x — o relato antigo só saía por chunk concluído e speed era fixo 0)
            Assert.Contains(reports, r => r.Fps > 0);
            Assert.Contains(reports, r => r.Speed > 0);
            // heartbeat (bug 22/09 b: rodapé congelado na largada do av1an) — com tick de
            // 50ms um encode de vários segundos TEM que produzir vários relatos
            Assert.True(reports.Count >= 5, $"relatos demais poucos: {reports.Count}");

            var probe = new ProbeService(IntegrationHelpers.Tools.FfprobePath!);
            var info = await probe.ProbeAsync(outPath);
            Assert.Equal("av1", FirstVideoCodec(outPath));     // stream é AV1
            Assert.InRange(info.DurationSeconds, 9.5, 10.5);   // corte de 10s respeitado
            Assert.NotEmpty(info.AudioStreams);                // áudio entrou no remux do av1an
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static string FirstVideoCodec(string file)
    {
        var (stdout, ok) = ProcessRunner.Capture(IntegrationHelpers.Tools.FfprobePath!,
            new[] { "-v", "error", "-select_streams", "v", "-show_entries", "stream=codec_name", "-of", "csv=p=0", file },
            60_000);
        return ok ? stdout.Trim() : "";
    }
}
