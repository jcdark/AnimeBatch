using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class EncodeServiceTests
{
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
        var (args, twoPass) = EncodeService.BuildVideoArgs(cfg, 500);

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
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500);

        Assert.Contains("p010le", args);
        Assert.Contains("-rc-lookahead 0", args);
        Assert.DoesNotContain("-tune", args); // tune none não vai
    }

    [Fact]
    public void Svt_bitrate_com_multipass_e_2pass_de_verdade()
    {
        var cfg = new CodecEncodeConfig { Code = "svt_av1_10bit", Preset = 6, UseConstantQuality = false, Multipass = true };
        var (args, twoPass) = EncodeService.BuildVideoArgs(cfg, 500);

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
        var (args, twoPass) = EncodeService.BuildVideoArgs(cfg, 500);

        Assert.False(twoPass);
        Assert.Contains("-crf 22", args);
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void Nvenc_cq_usa_cq_e_perfil_e_nivel()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", UseConstantQuality = true, Cq = 30, Profile = "2", Level = "4.0" };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500);

        Assert.Contains("-rc vbr -cq 30 -b:v 0", args);
        Assert.Contains("-profile:v 2", args);
        Assert.Contains("-level 4.0", args);
        Assert.DoesNotContain("-multipass", args); // no modo CQ o multipass fica oculto
    }

    [Fact]
    public void Nivel_auto_em_qualquer_capitalizacao_nao_vai_pro_encoder()
    {
        // regressão: o combo gravava o RÓTULO "Auto" e o ffmpeg rejeitava "-level Auto"
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Level = "Auto" };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500);
        Assert.DoesNotContain("-level", args);
    }

    [Fact]
    public void Nvenc_bitrate_emite_multipass_conforme_config()
    {
        var cfgOn = new CodecEncodeConfig { Code = "nvenc_av1", UseConstantQuality = false, Multipass = true };
        var (argsOn, _) = EncodeService.BuildVideoArgs(cfgOn, 500);
        Assert.Contains("-multipass fullres", argsOn);

        var cfgOff = cfgOn with { Multipass = false };
        var (argsOff, _) = EncodeService.BuildVideoArgs(cfgOff, 500);
        Assert.Contains("-multipass disabled", argsOff);
    }

    // ---- Reforço de qualidade (NvencBoost) ----

    [Fact]
    public void Nvenc_boost_full_emite_aq_uhq_e_analise_estendida()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 7, Tune = "hq", QualityBoost = true };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500, cudaGpu: 1, boost: NvencBoost.Full);

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
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500, boost: NvencBoost.Full);

        Assert.Contains("-tune ll", args);
        Assert.DoesNotContain("uhq", args);
    }

    [Fact]
    public void Nvenc_boost_aqonly_nao_inclui_analise_estendida()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", QualityBoost = true };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500, boost: NvencBoost.AqOnly);

        Assert.Contains("-spatial-aq 1", args);
        Assert.DoesNotContain("uhq", args);
        Assert.DoesNotContain("-tf_level", args);
        Assert.DoesNotContain("-lookahead_level", args);
    }

    [Fact]
    public void Nvenc_boost_off_mantem_argumentos_classicos()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", Preset = 5, Tune = "hq" };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500);

        Assert.Contains("-tune hq", args);
        Assert.DoesNotContain("-spatial-aq", args);
        Assert.DoesNotContain("-gpu", args);
    }

    [Fact]
    public void FastConversion_anula_o_boost_mesmo_pedindo_full()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1", FastConversion = true, QualityBoost = true };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 500, boost: NvencBoost.Full);

        Assert.Contains("-rc-lookahead 0", args);
        Assert.DoesNotContain("-spatial-aq", args);
    }

    [Fact]
    public void Nvenc_boost_funciona_no_modo_cq()
    {
        var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", UseConstantQuality = true, Cq = 28, QualityBoost = true };
        var (args, _) = EncodeService.BuildVideoArgs(cfg, 0, boost: NvencBoost.Full);

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
}
