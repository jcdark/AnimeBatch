using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Integração REAL do pipeline de upscale (ffmpeg + realcugan/ONNX de tools\): gera um
/// vídeo sintético, roda o pipeline completo (chunks → concat → mux) e confere o
/// resultado. Pega os bugs que só aparecem com processo de verdade (FileName vazio,
/// pasta de frames inexistente, -t do áudio, etc). Sem tools\, os testes PULAM.
/// </summary>
public class UpscalePipelineIntegrationTests : IDisposable
{
    private readonly string _workDir;

    public UpscalePipelineIntegrationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "animebatch_upscale_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
                Directory.Delete(_workDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [SkippableFact]
    public async Task Pipeline_completo_amplia_2s_e_muxa_audio()
    {
        IntegrationHelpers.SkipIfNoCugan();

        var src = Path.Combine(_workDir, "src.mp4");
        IntegrationHelpers.RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=24:duration=2 " +
                  $"-f lavfi -i sine=frequency=440:duration=2 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {IntegrationHelpers.Quote(src)}");

        var output = Path.Combine(_workDir, "out.mkv");
        var service = new UpscaleService(IntegrationHelpers.Tools.FfmpegPath!);
        var reports = new List<EncodeProgress>();
        var progress = new Progress<EncodeProgress>(reports.Add);

        // 180p → 720p = fator 4 (teto do modelo); 2s = 1 chunk = par de tamanho 1
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 2, Path.Combine(_workDir, "ups"), output,
            SourceHeight: 180, SourceFps: 24, TargetHeight: 720,
            ModelCode: "realcugan", UpscalerExePath: IntegrationHelpers.Tools.RealCuganPath!,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false),
            CancellationToken.None, progress);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");
        Assert.True(reports.Count > 0, "nenhum relatório de progresso foi emitido");
        Assert.True(reports.Max(r => r.OutTimeSeconds) > 1.5, "progresso não chegou perto de 100%");

        var info = await Probe(output);
        Assert.Equal(1280, info.Width);  // 4x de 320
        Assert.Equal(720, info.Height);  // alvo exato
        Assert.Equal(2, info.DurationSeconds, 0); // -t do mux corta na duração da região
        Assert.NotEmpty(info.AudioStreams);       // áudio da origem entrou no mux final
    }

    [SkippableFact]
    public async Task Pipeline_com_varios_chunks_usa_par_de_upscalers_concorrentes()
    {
        IntegrationHelpers.SkipIfNoCugan();

        // 35s = 2 chunks (30s + 5s) → exercita o pool de workers com chunks de tamanhos diferentes
        var src = Path.Combine(_workDir, "src35.mp4");
        IntegrationHelpers.RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=24:duration=35 " +
                  $"-f lavfi -i sine=frequency=440:duration=35 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {IntegrationHelpers.Quote(src)}");

        var output = Path.Combine(_workDir, "out35.mkv");
        var service = new UpscaleService(IntegrationHelpers.Tools.FfmpegPath!);

        // GpuIds com 2 entradas → 2 workers (ambos na mesma placa no ambiente de teste)
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 35, Path.Combine(_workDir, "ups35"), output,
            SourceHeight: 180, SourceFps: 24, TargetHeight: 720,
            ModelCode: "realcugan", UpscalerExePath: IntegrationHelpers.Tools.RealCuganPath!,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0, 0]),
            CancellationToken.None, null);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");

        var info = await Probe(output);
        Assert.Equal(35, info.DurationSeconds, 0); // os chunks emendados cobrem o arquivo inteiro
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.NotEmpty(info.AudioStreams);
    }

    [SkippableFact]
    public async Task Fonte_vfr_mantem_audio_e_video_sincronizados()
    {
        IntegrationHelpers.SkipIfNoCugan();

        // VFR sintético: concat FILTER re-encoda com timeline limpa (o concat demuxer -c copy
        // entre mp4s de timebases distintas gera timestamps quebrados). 10s a 30fps + 5s a 20fps
        // = 15s com taxas variáveis; áudio contínuo de 15s. Sem CFR na extração, o remontar a
        // taxa fixa deslocava o vídeo contra o áudio (bug real da fonte VFR do usuário).
        var src = Path.Combine(_workDir, "vfr.mkv");
        IntegrationHelpers.RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=30:duration=10 " +
                  $"-f lavfi -i testsrc2=size=320x180:rate=20:duration=5 " +
                  $"-f lavfi -i sine=frequency=440:duration=15 " +
                  $"-filter_complex \"[0:v][1:v]concat=n=2:v=1[v]\" " +
                  $"-map \"[v]\" -map 2:a -c:v libx264 -pix_fmt yuv420p -c:a aac {IntegrationHelpers.Quote(src)}");

        var output = Path.Combine(_workDir, "vfr_out.mkv");
        var service = new UpscaleService(IntegrationHelpers.Tools.FfmpegPath!);
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 15, Path.Combine(_workDir, "ups_vfr"), output,
            SourceHeight: 180, SourceFps: 25, TargetHeight: 720,
            ModelCode: "realcugan", UpscalerExePath: IntegrationHelpers.Tools.RealCuganPath!,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false),
            CancellationToken.None, null);

        var info = await Probe(output);
        // vídeo e áudio com a MESMA duração (±0,3s) = sincronizados
        Assert.InRange(info.DurationSeconds, 14.7, 15.3);
        Assert.NotEmpty(info.AudioStreams);
    }

    [SkippableFact]
    public async Task Pipeline_onnx_amplia_e_muxa_audio()
    {
        IntegrationHelpers.SkipIfNoOnnxModels();

        var src = Path.Combine(_workDir, "onnx_src.mp4");
        IntegrationHelpers.RunFfmpeg($"-f lavfi -i testsrc2=size=320x240:rate=24:duration=2 " +
                  $"-f lavfi -i sine=frequency=440:duration=2 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {IntegrationHelpers.Quote(src)}");

        var output = Path.Combine(_workDir, "onnx_out.mkv");
        var service = new UpscaleService(IntegrationHelpers.Tools.FfmpegPath!);
        var reports = new List<EncodeProgress>();
        var progress = new Progress<EncodeProgress>(reports.Add);

        // 240p → 720p = fator 3 → UM passe 2x (640x480) + lanczos pro alvo; DML na placa 0
        await service.UpscalePartOnnxAsync(new UpscalePartRequest(
            src, 0, 2, Path.Combine(_workDir, "ups_onnx"), output,
            SourceHeight: 240, SourceFps: 24, TargetHeight: 720,
            ModelCode: "onnx", UpscalerExePath: "",
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0], SourceWidth: 320, OnnxModelsDir: IntegrationHelpers.Tools.OnnxModelsDir),
            CancellationToken.None, progress);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");
        Assert.True(reports.Count > 0, "nenhum relatório de progresso foi emitido");

        var info = await Probe(output);
        Assert.Equal(960, info.Width);  // 640 do passe 2x → lanczos 720p
        Assert.Equal(720, info.Height);
        Assert.Equal(2, info.DurationSeconds, 0);
        Assert.NotEmpty(info.AudioStreams);
    }

    [SkippableFact]
    public async Task Pipeline_onnx_com_varios_chunks_e_dois_workers()
    {
        IntegrationHelpers.SkipIfNoOnnxModels();

        // 35s = 2 chunks → 2 workers rodando Run() CONCORRENTE na MESMA sessão (thread-safe)
        var src = Path.Combine(_workDir, "onnx35.mp4");
        IntegrationHelpers.RunFfmpeg($"-f lavfi -i testsrc2=size=320x240:rate=24:duration=35 " +
                  $"-f lavfi -i sine=frequency=440:duration=35 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {IntegrationHelpers.Quote(src)}");

        var output = Path.Combine(_workDir, "onnx35_out.mkv");
        var service = new UpscaleService(IntegrationHelpers.Tools.FfmpegPath!);

        await service.UpscalePartOnnxAsync(new UpscalePartRequest(
            src, 0, 35, Path.Combine(_workDir, "ups_onnx35"), output,
            SourceHeight: 240, SourceFps: 24, TargetHeight: 720,
            ModelCode: "onnx", UpscalerExePath: "",
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0, 0], SourceWidth: 320, OnnxModelsDir: IntegrationHelpers.Tools.OnnxModelsDir),
            CancellationToken.None, null);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");

        var info = await Probe(output);
        Assert.Equal(35, info.DurationSeconds, 0); // chunks emendados cobrem o arquivo inteiro
        Assert.Equal(960, info.Width);
        Assert.Equal(720, info.Height);
        Assert.NotEmpty(info.AudioStreams);
    }

    [SkippableFact]
    public void PickModelFile_escolhe_SD_ou_HD_pela_altura_da_origem()
    {
        IntegrationHelpers.SkipIfNoOnnxModels();

        var sd = OnnxUpscaleService.PickModelFile(IntegrationHelpers.Tools.OnnxModelsDir!, 480);
        Assert.Contains("SD_", Path.GetFileName(sd));
        Assert.True(File.Exists(sd));

        var hd = OnnxUpscaleService.PickModelFile(IntegrationHelpers.Tools.OnnxModelsDir!, 720);
        Assert.Contains("HD_", Path.GetFileName(hd));
        Assert.True(File.Exists(hd));
    }

    [SkippableFact]
    public async Task Fonte_ntsc_23976_mantem_video_e_audio_sincronizados_apos_varios_chunks()
    {
        IntegrationHelpers.SkipIfNoCugan();

        // 65s a 24000/1001 (todo anime NTSC-film): plano de frames = 719+719+120 = 1558 frames.
        // O corte antigo por -t 30 quantizava pelo fim do frame (719 por chunk) e o remontar a
        // -framerate fixa "acelerava" o vídeo ~4,2 ms POR CHUNK contra o áudio — ~200 ms de
        // desync numa parte de 24 min. Aqui vídeo e áudio precisam terminar juntos (±1,5 frame).
        var src = Path.Combine(_workDir, "ntsc.mkv");
        IntegrationHelpers.RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=24000/1001:duration=65 " +
                  $"-f lavfi -i sine=frequency=440:duration=65 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {IntegrationHelpers.Quote(src)}");

        var output = Path.Combine(_workDir, "ntsc_out.mkv");
        var service = new UpscaleService(IntegrationHelpers.Tools.FfmpegPath!);
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 65, Path.Combine(_workDir, "ups_ntsc"), output,
            SourceHeight: 180, SourceFps: 24000.0 / 1001, TargetHeight: 360,
            ModelCode: "realcugan", UpscalerExePath: IntegrationHelpers.Tools.RealCuganPath!,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0, 0], FpsRatio: "24000/1001"),
            CancellationToken.None, null);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");

        var (videoFrames, audioDuration) = StreamMeasurements(output);
        // 1558 frames exatos = plano 719+719+120; 1557 ou 1559 (±1 frame ≈ 42 ms) falham
        Assert.Equal(1558, videoFrames);
        var videoDuration = videoFrames * 1001.0 / 24000.0; // = 64,9816s
        Assert.True(Math.Abs(videoDuration - audioDuration) <= 1.5 * 1001.0 / 24000.0,
            $"vídeo {videoDuration:0.000}s vs áudio {audioDuration:0.000}s — desync acima de 1,5 frame");
    }

    /// <summary>Frames de vídeo (contados por decode) e duração do áudio (pacotes AAC ×
    /// 1024/rate) — o par é o termômetro do sync. Matroska não expõe duração por stream.</summary>
    private static (int VideoFrames, double AudioDuration) StreamMeasurements(string file)
    {
        var csv = ProcessRunner.Capture(IntegrationHelpers.Tools.FfprobePath!,
            new[] { "-v", "error", "-count_packets", "-show_entries", "stream=codec_type,nb_read_packets,sample_rate", "-of", "csv=p=0", file },
            120_000);
        Skip.IfNot(csv.Ok, "ffprobe não respondeu");

        int videoFrames = 0;
        double audioDuration = 0;
        foreach (var line in csv.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // ordem dos campos é por stream: "video,<packets>" e "audio,<rate>,<packets>"
            var parts = line.Split(',');
            if (parts[0] == "video" && parts.Length >= 2 &&
                int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var vf))
                videoFrames = vf;
            else if (parts[0] == "audio" && parts.Length >= 3 &&
                     long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate > 0 &&
                     long.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var packets))
                audioDuration = packets * 1024.0 / rate; // AAC: 1024 amostras por pacote
        }
        return (videoFrames, audioDuration);
    }

    private static async Task<EpisodeInfo> Probe(string output)
    {
        var probe = new ProbeService(IntegrationHelpers.Tools.FfprobePath!);
        return await probe.ProbeAsync(output);
    }
}
