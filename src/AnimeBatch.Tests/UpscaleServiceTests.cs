using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class UpscaleServiceTests
{
    [Theory]
    [InlineData(480, 1080, 3)]   // 2,25x → teto inteiro acima (2x+lanczos foi MAIS LENTO na prática — revertido)
    [InlineData(720, 1080, 2)]   // 1,5x → 2x
    [InlineData(720, 2160, 3)]   // 3x exato
    [InlineData(1080, 2160, 2)]  // 2x exato
    [InlineData(540, 1440, 3)]   // 2,67x
    [InlineData(300, 2160, 4)]   // 7,2x → teto do modelo
    public void Plano_amplia_com_menor_fator_inteiro_acima(int sourceHeight, int targetHeight, int expectedScale)
    {
        var plan = UpscaleService.SelectPlan(sourceHeight, targetHeight);

        Assert.True(plan.NeedsModel);
        Assert.Equal(expectedScale, plan.ModelScale);
        Assert.Equal(targetHeight, plan.TargetHeight);
    }

    [Theory]
    [InlineData(1080, 1080)]
    [InlineData(1080, 720)]
    public void Plano_sem_ampliacao_nao_passa_pelo_modelo(int sourceHeight, int targetHeight)
    {
        var plan = UpscaleService.SelectPlan(sourceHeight, targetHeight);

        Assert.False(plan.NeedsModel);
        Assert.Equal(targetHeight, plan.TargetHeight);
    }

    [Theory]
    [InlineData("24000/1001", 23.976)]
    [InlineData("24/1", 24.0)]
    [InlineData("30000/1001", 29.97)]
    [InlineData("25/1", 25.0)]
    public void Interpreta_fração_de_fps_do_ffprobe(string ratio, double expected)
    {
        Assert.Equal(expected, ProbeService.ParseFps(ratio), 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0/0")]
    [InlineData("abc")]
    public void Fps_invalido_retorna_zero(string? ratio)
    {
        Assert.Equal(0, ProbeService.ParseFps(ratio));
    }

    [Fact]
    public void Realesrgan_usa_animevideov3_com_pasta_de_modelos_ao_lado()
    {
        var exe = @"tools\realesrgan\realesrgan-ncnn-vulkan.exe";
        var args = UpscaleService.BuildUpscaleArgs(exe, "realesrgan", @"w\in", @"w\out", 3, gpuId: 0);

        Assert.Contains("-n", args);
        Assert.Contains("realesr-animevideov3", args);
        Assert.Contains("-s", args);
        Assert.Contains("3", args);
        Assert.Contains(@"tools\realesrgan\models", args); // -m
        Assert.Contains("-g", args);
        Assert.Contains("0", args);         // GPU principal fixada
        Assert.Contains("-j", args);
        Assert.Contains("1:2:8", args);     // save de PNG é o gargalo — 8 threads
        Assert.Contains("-f", args);
        Assert.Contains("png", args);
    }

    [Fact]
    public void Realesrgan_aceita_segunda_gpu_pelo_indice_vulkan()
    {
        var exe = @"tools\realesrgan\realesrgan-ncnn-vulkan.exe";
        var args = UpscaleService.BuildUpscaleArgs(exe, "realesrgan", @"w\in", @"w\out", 3, gpuId: 2);

        var g = args[Array.IndexOf(args, "-g") + 1];
        Assert.Equal("2", g); // nesta máquina: 2 = RTX 3060
    }

    [Fact]
    public void AutoWorkerGpus_da_worker_extra_a_placa_principal()
    {
        // dupla da máquina do usuário: 5060 Ti fica com 2 workers, 3060 com 1
        Assert.Equal([0, 2, 0], UpscaleService.AutoWorkerGpus([0, 2]));

        // sem segunda placa: duas instâncias na mesma GPU (uma só não satura)
        Assert.Equal([0, 0], UpscaleService.AutoWorkerGpus([]));
        Assert.Equal([2, 2], UpscaleService.AutoWorkerGpus([2]));

        // três placas: uma por placa + extra na principal
        Assert.Equal([0, 2, 3, 0], UpscaleService.AutoWorkerGpus([0, 2, 3]));
    }

    [Fact]
    public void Realcugan_2x_usa_denoise_1_e_3x_usa_denoise_3()
    {
        var exe = @"tools\realcugan\realcugan-ncnn-vulkan-20220728-windows\realcugan-ncnn-vulkan.exe";
        var args2 = UpscaleService.BuildUpscaleArgs(exe, "realcugan", @"w\in", @"w\out", 2, gpuId: 0);
        var args3 = UpscaleService.BuildUpscaleArgs(exe, "realcugan", @"w\in", @"w\out", 3, gpuId: 0);

        // models-se tem denoise1x/2x só em 2x; em 3x/4x o único denoise disponível é o 3x
        var n2 = args2[Array.IndexOf(args2, "-n") + 1];
        var n3 = args3[Array.IndexOf(args3, "-n") + 1];
        Assert.Equal("1", n2);
        Assert.Equal("3", n3);
        Assert.Contains(args2, a => a.Contains("models-se")); // -m aponta pro models-se ao lado do exe
    }

    [Fact]
    public void Extracao_gera_png_sequencial_em_cfr_com_seek_quando_ha_intervalo()
    {
        var framesDir = @"w\frames";
        var args = UpscaleService.BuildExtractArgs(@"src\ep.mkv", 91.5, 750, framesDir, "25/1");

        Assert.Equal(@"w\frames\%08d.png", args[^1]); // saída sempre por último
        Assert.Contains("-fps_mode", args);
        Assert.Contains("cfr", args);        // VFR da origem é reamostrado no tempo (sync do áudio)
        Assert.Contains("-r", args);
        Assert.Contains("25/1", args);       // taxa exata do probe
        Assert.Contains("91.5", args);
        Assert.Contains("-frames:v", args);  // corte por CONTAGEM de frames (não por -t)
        Assert.Contains("750", args);
        Assert.Contains("-pix_fmt", args);

        var whole = UpscaleService.BuildExtractArgs(@"src\ep.mkv", 0, 0, framesDir, "23.976");
        Assert.DoesNotContain("-ss", whole);
        Assert.DoesNotContain("-frames:v", whole);
    }

    [Fact]
    public void RateArg_prefere_a_fração_do_probe_e_cai_pro_decimal()
    {
        Assert.Equal("24000/1001", UpscaleService.RateArg(23.976023976023978, "24000/1001"));
        Assert.Equal("23.976", UpscaleService.RateArg(23.976023976023978, ""));
        Assert.Equal("24", UpscaleService.RateArg(24, null!));
        Assert.Equal("30000/1001", UpscaleService.RateArg(29.97, " 30000/1001 ")); // trim
    }

    [Theory]
    [InlineData(65, 23.976023976023978, 1558, 3, 719, 719, 120)]  // anime NTSC: chunk de 30s = 719 frames
    [InlineData(65, 24, 1560, 3, 720, 720, 120)]                  // fps inteiro: 720 exatos
    [InlineData(30, 25, 750, 1, 750, 0, 0)]                       // caberia em 1 chunk
    [InlineData(91.5, 30, 2745, 4, 900, 900, 900)]                // 3 cheios + cauda de 45 frames
    public void Plano_de_chunks_por_frames_cobre_a_duracao_exata(
        double totalSeconds, double fps, long totalFrames, int chunkCount,
        int f0, int f1, int f2)
    {
        var chunks = UpscaleService.PlanFrameChunks(totalSeconds, fps);

        Assert.Equal(chunkCount, chunks.Count);
        Assert.Equal(totalFrames, chunks.Sum(c => (long)c.FrameCount)); // soma = duração exata
        var perChunk = chunks.Select(c => c.FrameCount).ToList();
        Assert.Equal(f0, perChunk[0]);
        if (chunkCount > 1) Assert.Equal(f1, perChunk[1]);
        if (chunkCount > 2) Assert.Equal(f2, perChunk[2]);

        // offsets em segundos ficam na grade de frames (erro << 1 µs)
        for (var k = 0; k < chunks.Count; k++)
        {
            var expected = Math.Round(k * Math.Round(30 * fps) / fps, 9);
            Assert.Equal(expected, Math.Round(chunks[k].StartOffsetSeconds, 9));
        }
    }

    [Fact]
    public void Remontar_lossless_gera_intermediario_x264_qp0_sem_capitulos_e_com_aac()
    {
        var args = UpscaleService.BuildAssembleArgs(
            @"w\frames_out", "24000/1001", @"src\ep.mkv", @"w\ups_01.mkv",
            losslessIntermediate: true, copyAudio: false, keepChapters: false, copySubtitles: false,
            targetHeight: 0, durationSeconds: 30);

        Assert.Equal(@"w\ups_01.mkv", args[^1]); // saída sempre por último
        Assert.Contains("-framerate", args);
        Assert.Contains("24000/1001", args);     // remontar usa a taxa exata (anti-drift)
        Assert.Contains("-qp", args);
        Assert.Contains("0", args);
        Assert.Contains("-t", args);
        Assert.Contains("30", args); // -t corta o áudio da origem na duração da parte
        Assert.Contains("-map_chapters", args);
        Assert.Contains("-c:a", args);
        Assert.Contains("aac", args);
        Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-vf", args); // altura do modelo já é o alvo → sem filtro
    }

    [Fact]
    public void Remontar_final_do_modo_only_copia_audio_legendas_e_mantem_capitulos()
    {
        var args = UpscaleService.BuildAssembleArgs(
            @"w\frames_out", "23.976", @"src\ep.mkv", @"out\ep (upscaling).mkv",
            losslessIntermediate: false, copyAudio: true, keepChapters: true, copySubtitles: true,
            targetHeight: 1080, durationSeconds: null);

        Assert.Equal(@"out\ep (upscaling).mkv", args[^1]);
        Assert.Contains("-crf", args);
        Assert.Contains("14", args);
        Assert.Contains("copy", args);
        Assert.DoesNotContain("-t", args);     // arquivo inteiro: sem corte
        Assert.DoesNotContain("-map_chapters", args); // capítulos originais preservados
        Assert.Contains("scale=-2:1080:flags=lanczos", args);
    }

    [Fact]
    public void Plano_de_ampliacao_nao_exata_usa_lanczos_no_remontar()
    {
        // 480p × 3 = 1440p > 1080p → o remontar reduz com lanczos pro alvo exato
        var plan = UpscaleService.SelectPlan(480, 1080);
        Assert.Equal(3, plan.ModelScale);
        Assert.Equal(1080, plan.TargetHeight);

        // quando o modelo entrega exatamente a altura alvo, o chamador passa 0 (sem filtro)
        var exact = UpscaleService.SelectPlan(540, 1620); // 3x exato
        Assert.Equal(3, exact.ModelScale);
        Assert.Equal(1620, 540 * exact.ModelScale);
    }

    [Fact]
    public void Concat_emenda_segmentos_sem_reencode_e_saida_por_ultimo()
    {
        var args = UpscaleService.BuildConcatArgs(@"w\concat.txt", @"w\concat.mkv");

        Assert.Equal(@"w\concat.mkv", args[^1]);
        Assert.Contains("-f", args);
        Assert.Contains("concat", args);
        Assert.Contains("-safe", args);
        Assert.Contains("0", args);
        Assert.Contains("-c", args);
        Assert.Contains("copy", args);
    }

    [Fact]
    public void Lista_do_concat_escapa_apostrofos_e_monta_linhas_file()
    {
        var content = UpscaleService.BuildConcatListContent([@"w\seg_0000.mkv", @"w\meu'ep.mkv"]);
        var lines = content.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("file 'w\\seg_0000.mkv'", lines[0]);
        Assert.Equal("file 'w\\meu'\\''ep.mkv'", lines[1]); // apóstrofo escapado do demuxer
    }

    [Fact]
    public void Mux_final_do_intermediario_corta_audio_da_origem_e_solta_capitulos()
    {
        var args = UpscaleService.BuildFinalMuxArgs(
            @"w\concat.mkv", @"src\ep.mkv", @"w\ups_01.mkv",
            startSeconds: 91.5, durationSeconds: 30,
            copyAudio: false, keepChapters: false, copySubtitles: false);

        Assert.Equal(@"w\ups_01.mkv", args[^1]);
        Assert.Contains("91.5", args); // -ss da região na origem
        Assert.Contains("-t", args);
        Assert.Contains("-map_chapters", args);
        Assert.Contains("aac", args);
        Assert.DoesNotContain("copy", args.Where(a => a.StartsWith("-c:a", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void Mux_final_do_modo_only_copia_audio_e_preserva_capitulos()
    {
        var args = UpscaleService.BuildFinalMuxArgs(
            @"w\concat.mkv", @"src\ep.mkv", @"out\ep (upscaling).mkv",
            startSeconds: null, durationSeconds: null,
            copyAudio: true, keepChapters: true, copySubtitles: true);

        Assert.Equal(@"out\ep (upscaling).mkv", args[^1]);
        Assert.Contains("-c:a", args);
        Assert.Contains("copy", args);
        Assert.Contains("-c:s", args);
        Assert.DoesNotContain("-t", args);
        Assert.DoesNotContain("-map_chapters", args);
        Assert.DoesNotContain("-ss", args);
    }
}
