using AnimeBatch.Core.Services;
using System.Text;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Integração REAL do pipeline de upscale (ffmpeg + realcugan de tools\): gera um vídeo
/// sintético de 2s, roda UpscalePartAsync completo (chunks → concat → mux) e confere o
/// resultado. Pega os bugs que só aparecem com processo de verdade (FileName vazio,
/// pasta de frames inexistente, -t do áudio, etc).
/// </summary>
public class UpscalePipelineIntegrationTests : IDisposable
{
    private readonly string _workDir;
    private readonly ToolsLocator _tools;

    public UpscalePipelineIntegrationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "animebatch_upscale_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _tools = new ToolsLocator(FindRepoToolsDir());
    }

    /// <summary>Acha tools\ da raiz do repo subindo do bin de teste (a ToolsLocator padrão
    /// sobe só 6 níveis, um a menos do que o bin\...\net9.0 precisa).</summary>
    private static string? FindRepoToolsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
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

    private string? Ffmpeg => _tools.FfmpegPath;
    private string? Cugan => _tools.RealCuganPath;

    [Fact]
    public async Task Pipeline_completo_amplia_2s_e_muxa_audio()
    {
        // ferramentas são obrigatórias neste projeto (repo tools\) — se faltarem, o teste falha
        Assert.True(Ffmpeg is not null, "ffmpeg não encontrado pela ToolsLocator");
        Assert.True(Cugan is not null, "realcugan não encontrado pela ToolsLocator");

        var src = Path.Combine(_workDir, "src.mp4");
        RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=24:duration=2 " +
                  $"-f lavfi -i sine=frequency=440:duration=2 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {Quote(src)}");

        var output = Path.Combine(_workDir, "out.mkv");
        var service = new UpscaleService(Ffmpeg);
        var reports = new List<EncodeProgress>();
        var progress = new Progress<EncodeProgress>(reports.Add);

        // 180p → 720p = fator 4 (teto do modelo); 2s = 1 chunk = par de tamanho 1
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 2, Path.Combine(_workDir, "ups"), output,
            SourceHeight: 180, SourceFps: 24, TargetHeight: 720,
            ModelCode: "realcugan", UpscalerExePath: Cugan,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false),
            CancellationToken.None, progress);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");
        Assert.True(reports.Count > 0, "nenhum relatório de progresso foi emitido");
        Assert.True(reports.Max(r => r.OutTimeSeconds) > 1.5, "progresso não chegou perto de 100%");

        var probe = new ProbeService(_tools.FfprobePath!);
        var info = await probe.ProbeAsync(output);
        Assert.Equal(1280, info.Width);  // 4x de 320
        Assert.Equal(720, info.Height);  // alvo exato
        Assert.Equal(2, info.DurationSeconds, 0); // -t do mux corta na duração da região
        Assert.NotEmpty(info.AudioStreams);       // áudio da origem entrou no mux final
    }

    [Fact]
    public async Task Pipeline_com_varios_chunks_usa_par_de_upscalers_concorrentes()
    {
        Assert.True(Ffmpeg is not null, "ffmpeg não encontrado pela ToolsLocator");
        Assert.True(Cugan is not null, "realcugan não encontrado pela ToolsLocator");

        // 35s = 2 chunks (30s + 5s) → exercita o pool de workers com chunks de tamanhos diferentes
        var src = Path.Combine(_workDir, "src35.mp4");
        RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=24:duration=35 " +
                  $"-f lavfi -i sine=frequency=440:duration=35 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {Quote(src)}");

        var output = Path.Combine(_workDir, "out35.mkv");
        var service = new UpscaleService(Ffmpeg);

        // GpuIds com 2 entradas → 2 workers (ambos na mesma placa no ambiente de teste)
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 35, Path.Combine(_workDir, "ups35"), output,
            SourceHeight: 180, SourceFps: 24, TargetHeight: 720,
            ModelCode: "realcugan", UpscalerExePath: Cugan,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0, 0]),
            CancellationToken.None, null);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");

        var probe = new ProbeService(_tools.FfprobePath!);
        var info = await probe.ProbeAsync(output);
        Assert.Equal(35, info.DurationSeconds, 0); // os chunks emendados cobrem o arquivo inteiro
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.NotEmpty(info.AudioStreams);
    }

    [Fact]
    public async Task Fonte_vfr_mantem_audio_e_video_sincronizados()
    {
        Assert.True(Ffmpeg is not null, "ffmpeg não encontrado pela ToolsLocator");
        Assert.True(Cugan is not null, "realcugan não encontrado pela ToolsLocator");

        // VFR sintético: concat FILTER re-encoda com timeline limpa (o concat demuxer -c copy
        // entre mp4s de timebases distintas gera timestamps quebrados). 10s a 30fps + 5s a 20fps
        // = 15s com taxas variáveis; áudio contínuo de 15s. Sem CFR na extração, o remontar a
        // taxa fixa deslocava o vídeo contra o áudio (bug real da fonte VFR do usuário).
        var src = Path.Combine(_workDir, "vfr.mkv");
        RunFfmpeg($"-f lavfi -i testsrc2=size=320x180:rate=30:duration=10 " +
                  $"-f lavfi -i testsrc2=size=320x180:rate=20:duration=5 " +
                  $"-f lavfi -i sine=frequency=440:duration=15 " +
                  $"-filter_complex \"[0:v][1:v]concat=n=2:v=1[v]\" " +
                  $"-map \"[v]\" -map 2:a -c:v libx264 -pix_fmt yuv420p -c:a aac {Quote(src)}");

        var output = Path.Combine(_workDir, "vfr_out.mkv");
        var service = new UpscaleService(Ffmpeg);
        await service.UpscalePartAsync(new UpscalePartRequest(
            src, 0, 15, Path.Combine(_workDir, "ups_vfr"), output,
            SourceHeight: 180, SourceFps: 25, TargetHeight: 720,
            ModelCode: "realcugan", UpscalerExePath: Cugan,
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false),
            CancellationToken.None, null);

        var probe = new ProbeService(_tools.FfprobePath!);
        var info = await probe.ProbeAsync(output);
        // vídeo e áudio com a MESMA duração (±0,3s) = sincronizados
        Assert.InRange(info.DurationSeconds, 14.7, 15.3);
        Assert.NotEmpty(info.AudioStreams);
    }

    [Fact]
    public async Task Pipeline_onnx_amplia_e_muxa_audio()
    {
        Assert.True(Ffmpeg is not null, "ffmpeg não encontrado pela ToolsLocator");
        Assert.True(_tools.OnnxModelsDir is not null, "tools\\models-onnx não encontrado pela ToolsLocator");

        var src = Path.Combine(_workDir, "onnx_src.mp4");
        RunFfmpeg($"-f lavfi -i testsrc2=size=320x240:rate=24:duration=2 " +
                  $"-f lavfi -i sine=frequency=440:duration=2 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {Quote(src)}");

        var output = Path.Combine(_workDir, "onnx_out.mkv");
        var service = new UpscaleService(Ffmpeg);
        var reports = new List<EncodeProgress>();
        var progress = new Progress<EncodeProgress>(reports.Add);

        // 240p → 720p = fator 3 → UM passe 2x (640x480) + lanczos pro alvo; DML na placa 0
        await service.UpscalePartOnnxAsync(new UpscalePartRequest(
            src, 0, 2, Path.Combine(_workDir, "ups_onnx"), output,
            SourceHeight: 240, SourceFps: 24, TargetHeight: 720,
            ModelCode: "onnx", UpscalerExePath: "",
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0], SourceWidth: 320, OnnxModelsDir: _tools.OnnxModelsDir),
            CancellationToken.None, progress);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");
        Assert.True(reports.Count > 0, "nenhum relatório de progresso foi emitido");

        var probe = new ProbeService(_tools.FfprobePath!);
        var info = await probe.ProbeAsync(output);
        Assert.Equal(960, info.Width);  // 640 do passe 2x → lanczos 720p
        Assert.Equal(720, info.Height);
        Assert.Equal(2, info.DurationSeconds, 0);
        Assert.NotEmpty(info.AudioStreams);
    }

    [Fact]
    public async Task Pipeline_onnx_com_varios_chunks_e_dois_workers()
    {
        Assert.True(Ffmpeg is not null, "ffmpeg não encontrado pela ToolsLocator");
        Assert.True(_tools.OnnxModelsDir is not null, "tools\\models-onnx não encontrado pela ToolsLocator");

        // 35s = 2 chunks → 2 workers rodando Run() CONCORRENTE na MESMA sessão (thread-safe)
        var src = Path.Combine(_workDir, "onnx35.mp4");
        RunFfmpeg($"-f lavfi -i testsrc2=size=320x240:rate=24:duration=35 " +
                  $"-f lavfi -i sine=frequency=440:duration=35 " +
                  $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest {Quote(src)}");

        var output = Path.Combine(_workDir, "onnx35_out.mkv");
        var service = new UpscaleService(Ffmpeg);

        await service.UpscalePartOnnxAsync(new UpscalePartRequest(
            src, 0, 35, Path.Combine(_workDir, "ups_onnx35"), output,
            SourceHeight: 240, SourceFps: 24, TargetHeight: 720,
            ModelCode: "onnx", UpscalerExePath: "",
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            GpuIds: [0, 0], SourceWidth: 320, OnnxModelsDir: _tools.OnnxModelsDir),
            CancellationToken.None, null);

        Assert.True(File.Exists(output), "arquivo de saída não foi gerado");

        var probe = new ProbeService(_tools.FfprobePath!);
        var info = await probe.ProbeAsync(output);
        Assert.Equal(35, info.DurationSeconds, 0); // chunks emendados cobrem o arquivo inteiro
        Assert.Equal(960, info.Width);
        Assert.Equal(720, info.Height);
        Assert.NotEmpty(info.AudioStreams);
    }

    [Fact]
    public void PickModelFile_escolhe_SD_ou_HD_pela_altura_da_origem()
    {
        Assert.True(_tools.OnnxModelsDir is not null, "tools\\models-onnx não encontrado pela ToolsLocator");

        var sd = OnnxUpscaleService.PickModelFile(_tools.OnnxModelsDir!, 480);
        Assert.Contains("SD_", Path.GetFileName(sd));
        Assert.True(File.Exists(sd));

        var hd = OnnxUpscaleService.PickModelFile(_tools.OnnxModelsDir!, 720);
        Assert.Contains("HD_", Path.GetFileName(hd));
        Assert.True(File.Exists(hd));
    }

    private void RunFfmpeg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in SplitArgs(args))
            psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg de teste falhou: {stderr}");
    }

    private static string Quote(string s) => "\"" + s + "\"";

    private static IEnumerable<string> SplitArgs(string line)
    {
        // parser simples: tokens entre aspas ficam inteiros
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }
}
