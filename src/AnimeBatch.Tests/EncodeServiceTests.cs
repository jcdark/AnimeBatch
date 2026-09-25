using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class EncodeServiceTests
{
    /// <summary>Wrapper do teste: BuildVideoArgs devolve args DISCRETOS (um token por item,
    /// como vão para o Process.ArgumentList); os asserts abaixo enxergam a linha colada,
    /// que é o que o comando final representa.</summary>
    private static (string Args, bool TwoPass) BuildVideoArgs(
        CodecEncodeConfig cfg, int kbps, int? cudaGpu = null, NvencBoost boost = NvencBoost.Off)
    {
        var (args, twoPass) = EncodeService.BuildVideoArgs(cfg, kbps, cudaGpu, boost);
        return (string.Join(' ', args), twoPass);
    }

    [Fact]
    public void Args_sao_itens_discretos_nenhum_token_tem_espaco()
    {
        // um item de ArgumentList com espaço viraria UM argumento citado no ffmpeg
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", Preset = 7, Tune = "hq", QualityBoost = true, Multipass = true };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500, cudaGpu: 2, boost: NvencBoost.Full);
        Assert.All(args, a => Assert.DoesNotContain(' ', a));
        Assert.All(args, a => Assert.False(string.IsNullOrWhiteSpace(a)));
    }

    [Fact]
    public void Comando_nvenc_bitrate_completo_byte_identico()
    {
        // trava de regressão: a ordem e o conteúdo exatos do comando não podem mudar
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", Preset = 5, Tune = "hq", UseConstantQuality = false, Multipass = true };
        var (args, twoPass) = BuildVideoArgs(cfg, 500);

        Assert.False(twoPass);
        Assert.Equal(
            "-c:v av1_nvenc -preset p5 -tune hq -rc vbr -b:v 500k -maxrate 1200k -bufsize 2400k " +
            "-multipass fullres -rc-lookahead 32 -profile:v 0 -pix_fmt yuv420p",
            args);
    }

    [Fact]
    public void Comando_svt_2pass_byte_identico()
    {
        var cfg = new CodecEncodeConfig { Code = "svt_av1_10bit", Preset = 6, UseConstantQuality = false, Multipass = true };
        var (args, twoPass) = BuildVideoArgs(cfg, 500);

        Assert.True(twoPass);
        Assert.Equal("-c:v libsvtav1 -preset 6 -b:v 500k -profile:v 0 -pix_fmt yuv420p10le", args);
    }

    [Fact]
    public void Comando_nvenc_boost_full_byte_identico()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 7, Tune = "hq", QualityBoost = true };
        var (args, _) = BuildVideoArgs(cfg, 500, cudaGpu: 1, boost: NvencBoost.Full);

        Assert.Equal(
            "-c:v av1_nvenc -preset p7 -rc vbr -b:v 500k -maxrate 1200k -bufsize 2400k " +
            "-multipass fullres -rc-lookahead 32 -profile:v 0 -spatial-aq 1 -temporal-aq 1 -aq-strength 8 " +
            "-tune uhq -tf_level 4 -lookahead_level auto -gpu 1 -pix_fmt p010le",
            args);
    }

    [Theory]
    [InlineData("out_time_us=2500000", 10.0, 25.0)]
    [InlineData("out_time_us=10000000", 10.0, 100.0)]
    [InlineData("out_time_us=99999999", 10.0, 100.0)] // clamp no teto
    [InlineData("out_time_ms=5000000", 10.0, 50.0)]   // out_time_ms do ffmpeg tb é microssegundo
    public void Interpreta_linhas_de_progresso(string line, double duration, double expected)
    {
        Assert.True(EncodeService.TryParseProgressPercent(line, duration, out var percent));
        Assert.Equal(expected, percent, 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("progress=continue")]
    [InlineData("frame=123")]
    [InlineData(null)]
    public void Linhas_que_nao_sao_progresso_retornam_false(string? line)
    {
        Assert.False(EncodeService.TryParseProgressPercent(line, 10.0, out _));
    }

    [Fact]
    public void Linha_vazia_nao_estoura_e_duracao_zero_nao_calcula()
    {
        Assert.False(EncodeService.TryParseProgressPercent("", 0, out _));
        Assert.False(EncodeService.TryParseProgressPercent("out_time_us=100", 0, out _));
    }

    [Fact]
    public void Nvenc_bitrate_usa_vbr_com_teto_e_multipass()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", Preset = 5, Tune = "hq", UseConstantQuality = false, Multipass = true };
        var (args, twoPass) = BuildVideoArgs(cfg, 500);

        Assert.False(twoPass); // NVENC usa multipass interno, não 2-pass com statsfile
        Assert.Contains("av1_nvenc", args);
        Assert.Contains("-preset p5", args);
        Assert.Contains("-tune hq", args);
        Assert.Contains("-multipass fullres", args);
        Assert.Contains("-rc-lookahead 32", args);
        Assert.Contains("-b:v 500k", args);
        Assert.Contains("-maxrate 1200k", args);
        Assert.Contains("-pix_fmt yuv420p", args);
    }

    [Fact]
    public void Nvenc_10bit_e_fast_conversion()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 5, FastConversion = true };
        var (args, _) = BuildVideoArgs(cfg, 500);

        Assert.Contains("p010le", args);
        Assert.Contains("-rc-lookahead 0", args);
        Assert.DoesNotContain("-tune", args); // tune none não vai
    }

    [Fact]
    public void Svt_bitrate_com_multipass_e_2pass_de_verdade()
    {
        var cfg = new CodecEncodeConfig { Code = "svt_av1_10bit", Preset = 6, UseConstantQuality = false, Multipass = true };
        var (args, twoPass) = BuildVideoArgs(cfg, 500);

        Assert.True(twoPass); // igual ao script de referência: 2-pass no modo taxa de bits
        Assert.Contains("libsvtav1", args);
        Assert.Contains("-b:v 500k", args);
        Assert.DoesNotContain("-maxrate", args); // SVT não suporta VBV
        Assert.Contains("yuv420p10le", args);
    }

    [Fact]
    public void Svt_cq_usa_crf_e_e_single_pass()
    {
        var cfg = new CodecEncodeConfig { Code = "svt_av1", UseConstantQuality = true, Cq = 22, Multipass = true };
        var (args, twoPass) = BuildVideoArgs(cfg, 500);

        Assert.False(twoPass);
        Assert.Contains("-crf 22", args);
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void Nvenc_cq_usa_cq_e_perfil_e_nivel()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", UseConstantQuality = true, Cq = 30, Profile = "0", Level = "4.0" };
        var (args, _) = BuildVideoArgs(cfg, 500);

        Assert.Contains("-rc vbr -cq 30 -b:v 0", args);
        Assert.Contains("-profile:v 0", args);
        Assert.Contains("-level 4.0", args);
        Assert.DoesNotContain("-multipass", args); // no modo CQ o multipass fica oculto
    }

    [Theory]
    [InlineData("1")] // High = 4:4:4
    [InlineData("2")] // Professional = 4:2:2/12-bit — o SVT 4.x rejeitava com "bad parameter"
    public void Perfil_incompativel_com_av1_420_cai_para_main(string profileSalvo)
    {
        // AV1 4:2:0 só existe no Main (0); config antiga com High/Professional não pode
        // derrubar o encode com EINVAL (código -22)
        var cfg = new CodecEncodeConfig { Code = "svt_av1_10bit", Profile = profileSalvo };
        var (args, _) = BuildVideoArgs(cfg, 500);

        Assert.Contains("-profile:v 0", args);
        Assert.DoesNotContain("-profile:v 1", args);
        Assert.DoesNotContain("-profile:v 2", args);
    }

    [Fact]
    public void Perfil_none_nao_vai_pro_encoder()
    {
        var cfg = new CodecEncodeConfig { Code = "svt_av1", Profile = "none" };
        var (args, _) = BuildVideoArgs(cfg, 500);
        Assert.DoesNotContain("-profile:v", args);
    }

    // ---- Caminho HandBrakeCLI (motor primário de encode) ----

    [Fact]
    public void Handbrake_args_mirram_o_script_original()
    {
        var cfg = new CodecEncodeConfig
        {
            Code = "svt_av1_10bit", Preset = 5, UseConstantQuality = false, Multipass = true, TurboFirstPass = false,
        };
        var (args, multiPass) = EncodeService.BuildHandBrakeArgs(
            cfg, 450, "src.mkv", new EncodeService.JobItemRef(36, 125.999, 450), "out.mkv");

        Assert.True(multiPass);
        Assert.Equal(
            "-i src.mkv -o out.mkv --start-at seconds:36 --stop-at duration:89.999 -e svt_av1_10bit " +
            "--encoder-preset 5 -b 450 --multi-pass --audio 1 --aencoder av_aac --ab 160",
            string.Join(' ', args));
    }

    [Fact]
    public void Handbrake_turbo_na_primeira_passada_e_nome_do_codec_passthrough()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 7, TurboFirstPass = true };
        var (args, multiPass) = EncodeService.BuildHandBrakeArgs(
            cfg, 900, "s.mkv", new EncodeService.JobItemRef(0, 10, 900), "o.mkv");

        Assert.True(multiPass);
        Assert.Contains("--turbo", args);
        Assert.Contains("-e nvenc_av1_10bit", string.Join(' ', args)); // o nome do codec É o nome do encoder no HB
    }

    [Fact]
    public void Handbrake_conversao_rapida_tira_o_multipass_e_cq_usa_quality()
    {
        var rapida = new CodecEncodeConfig { Code = "svt_av1", FastConversion = true, Multipass = true };
        var (argsRapida, multiRapida) = EncodeService.BuildHandBrakeArgs(
            rapida, 500, "s.mkv", new EncodeService.JobItemRef(0, 10, 500), "o.mkv");
        Assert.False(multiRapida);
        Assert.DoesNotContain("--multi-pass", argsRapida);
        Assert.Contains("-b 500", string.Join(' ', argsRapida));

        var cq = new CodecEncodeConfig { Code = "svt_av1_10bit", UseConstantQuality = true, Cq = 28 };
        var (argsCq, multiCq) = EncodeService.BuildHandBrakeArgs(
            cq, 500, "s.mkv", new EncodeService.JobItemRef(0, 10, 500), "o.mkv");
        Assert.False(multiCq);
        Assert.Contains("--quality 28", string.Join(' ', argsCq));
        Assert.DoesNotContain("-b", argsCq);
    }

    [Fact]
    public void Nivel_auto_em_qualquer_capitalizacao_nao_vai_pro_encoder()
    {
        // regressão: o combo gravava o RÓTULO "Auto" e o ffmpeg rejeitava "-level Auto"
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Level = "Auto" };
        var (args, _) = BuildVideoArgs(cfg, 500);
        Assert.DoesNotContain("-level", args);
    }

    [Fact]
    public void Nvenc_bitrate_emite_multipass_conforme_config()
    {
        var cfgOn = new CodecEncodeConfig { Code = "nvenc_av1", UseConstantQuality = false, Multipass = true };
        var (argsOn, _) = BuildVideoArgs(cfgOn, 500);
        Assert.Contains("-multipass fullres", argsOn);

        var cfgOff = cfgOn with { Multipass = false };
        var (argsOff, _) = BuildVideoArgs(cfgOff, 500);
        Assert.Contains("-multipass disabled", argsOff);
    }

    // ---- Reforço de qualidade (NvencBoost) ----

    [Fact]
    public void Nvenc_boost_full_emite_aq_uhq_e_analise_estendida()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 7, Tune = "hq", QualityBoost = true };
        var (args, _) = BuildVideoArgs(cfg, 500, cudaGpu: 1, boost: NvencBoost.Full);

        Assert.Contains("-spatial-aq 1", args);
        Assert.Contains("-temporal-aq 1", args);
        Assert.Contains("-aq-strength 8", args);
        Assert.Contains("-tune uhq", args);          // UHQ substitui o hq
        Assert.DoesNotContain("-tune hq", args);
        Assert.Contains("-tf_level 4", args);
        Assert.Contains("-lookahead_level auto", args);
        Assert.Contains("-gpu 1", args);             // dirigido à placa NVENC 1
    }

    [Fact]
    public void Nvenc_boost_full_respeita_tune_escolhido_pelo_usuario()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", Tune = "ll", QualityBoost = true };
        var (args, _) = BuildVideoArgs(cfg, 500, boost: NvencBoost.Full);

        Assert.Contains("-tune ll", args);
        Assert.DoesNotContain("uhq", args);
    }

    [Fact]
    public void Nvenc_boost_aqonly_nao_inclui_analise_estendida()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", QualityBoost = true };
        var (args, _) = BuildVideoArgs(cfg, 500, boost: NvencBoost.AqOnly);

        Assert.Contains("-spatial-aq 1", args);
        Assert.DoesNotContain("uhq", args);
        Assert.DoesNotContain("-tf_level", args);
        Assert.DoesNotContain("-lookahead_level", args);
    }

    [Fact]
    public void Nvenc_boost_off_mantem_argumentos_classicos()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", Preset = 5, Tune = "hq" };
        var (args, _) = BuildVideoArgs(cfg, 500);

        Assert.Contains("-tune hq", args);
        Assert.DoesNotContain("-spatial-aq", args);
        Assert.DoesNotContain("-gpu", args);
    }

    [Fact]
    public void FastConversion_anula_o_boost_mesmo_pedindo_full()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", FastConversion = true, QualityBoost = true };
        var (args, _) = BuildVideoArgs(cfg, 500, boost: NvencBoost.Full);

        Assert.Contains("-rc-lookahead 0", args);
        Assert.DoesNotContain("-spatial-aq", args);
    }

    [Fact]
    public void Nvenc_boost_funciona_no_modo_cq()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", UseConstantQuality = true, Cq = 28, QualityBoost = true };
        var (args, _) = BuildVideoArgs(cfg, 0, boost: NvencBoost.Full);

        Assert.Contains("-rc vbr -cq 28 -b:v 0", args);
        Assert.Contains("-spatial-aq 1", args);
    }

    [Fact]
    public void Escada_de_variantes_do_boost_cai_ate_a_base()
    {
        var full = new CodecEncodeConfig { Code = "nvenc_av1", QualityBoost = true };
        var ladder = EncodeService.BoostVariants(full);
        Assert.Equal(3, ladder.Count);
        Assert.Equal(NvencBoost.Full, ladder[0].Boost);
        Assert.Equal(NvencBoost.AqOnly, ladder[1].Boost);
        Assert.Equal(NvencBoost.Off, ladder[2].Boost);

        // fora dessas condições, só a base — sem tentativas extras
        Assert.Single(EncodeService.BoostVariants(full with { FastConversion = true }));
        Assert.Single(EncodeService.BoostVariants(full with { QualityBoost = false }));
        Assert.Single(EncodeService.BoostVariants(full with { Code = "svt_av1_10bit" }));
    }

    // ---- Caminho Av1an (códigos av1an_*) ----

    [Fact]
    public void Av1an_args_crf_8bit_byte_identico()
    {
        var args = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1", Preset = 8, UseConstantQuality = true, Cq = 30, ScenecutMode = 0 },
            kbps: 500, "in.mkv", "out.mkv", "work\\av1an_out", "work\\pass_01_av1an.log",
            internalWorkers: 6, audioParams: "-c:a copy");

        Assert.Equal(
            "-i in.mkv -o out.mkv --temp work\\av1an_out -e svt-av1 -w 6 -y -l work\\pass_01_av1an.log " +
            "--no-defaults --pix-format yuv420p -v --keyint 240 --scd 0 --preset 8 --rc 0 --crf 30 -a -c:a copy",
            string.Join(' ', args));
    }

    [Fact]
    public void Av1an_scenecut_rapida_passa_downscale_720()
    {
        // modo 1 é o DEFAULT da config (configs antigas sem a chave ganham a análise rápida)
        var args = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1_10bit", Preset = 6 },
            450, "i", "o", "t", "l.log", 4, "-c:a copy");

        var joined = string.Join(' ', args);
        Assert.Contains("--sc-downscale-height 720", joined);
        Assert.DoesNotContain("--sc-method", joined);
    }

    [Fact]
    public void Av1an_scenecut_maxima_passa_downscale_360_e_metodo_fast()
    {
        var args = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1", ScenecutMode = 2 },
            500, "i", "o", "t", "l.log", 4, "-c:a copy");

        var joined = string.Join(' ', args);
        Assert.Contains("--sc-downscale-height 360", joined);
        Assert.Contains("--sc-method fast", joined);
    }

    [Fact]
    public void Av1an_bestsource_usa_chunk_method_vs_e_preciso_nao_passa_nada()
    {
        var com = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1", ScenecutMode = 0 },
            500, "i", "o", "t", "l.log", 4, "-c:a copy", bestSourceChunks: true);
        Assert.Contains("-m", com);
        Assert.Contains("bestsource", com);

        var sem = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1", ScenecutMode = 0 },
            500, "i", "o", "t", "l.log", 4, "-c:a copy");
        Assert.DoesNotContain("bestsource", sem);
    }

    [Fact]
    public void Av1an_args_10bit_com_2pass_bitrate()
    {
        var args = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1_10bit", Preset = 6, UseConstantQuality = false, Multipass = true },
            kbps: 450, "in.mkv", "out.mkv", "work\\tmp", "work\\l.log", internalWorkers: 4, audioParams: "-c:a aac -b:a 160k");

        var joined = string.Join(' ', args);
        // -p 2 só no modo bitrate+multipass; 10 bits no pix-format; rc/tbr no lugar do crf
        Assert.Contains(" -p 2 ", joined);
        Assert.Contains("--pix-format yuv420p10le", joined);
        Assert.Contains("--preset 6 --rc 1 --tbr 450", joined);
        Assert.DoesNotContain("--crf", joined);
        // GOP obrigatório: keyint 0 (default do av1an) quebra o VBR no svt 4.x
        Assert.Contains("--keyint 240", joined);
    }

    [Fact]
    public void Av1an_fastconversion_ou_cq_nao_tem_2pass()
    {
        var cq = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1", UseConstantQuality = true, Cq = 22 },
            500, "i", "o", "t", "l.log", 4, "-c:a copy");
        Assert.DoesNotContain("-p", cq);

        var fast = EncodeService.BuildAv1anArgs(
            new CodecEncodeConfig { Code = "av1an_av1", FastConversion = true },
            500, "i", "o", "t", "l.log", 4, "-c:a copy");
        Assert.DoesNotContain("-p", fast);
    }
}
