using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>Testes do motor ONNX (sem GPU): planejamento de passes, geometria de pad,
/// conversões RGB↔tensor, argumentos dos pipes crus e enumeração DXGI.</summary>
public class OnnxUpscaleServiceTests
{
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void PlanPasses_encadeia_segundo_passe_somente_no_fator_4(int planScale, int expectedPasses)
    {
        var passes = OnnxUpscaleService.PlanPasses("modelo.onnx", planScale);
        Assert.Equal(expectedPasses, passes.Count);
        Assert.All(passes, p => Assert.Equal(2, p.Scale));
        Assert.All(passes, p => Assert.Equal("modelo.onnx", p.ModelPath));
    }

    [Theory]
    [InlineData(852, 480, 856, 480)]
    [InlineData(64, 64, 64, 64)]
    [InlineData(851, 479, 856, 480)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(1920, 1080, 1920, 1080)]
    public void PaddedSize_arredonda_para_multiplo_de_8(int w, int h, int expectedW, int expectedH)
    {
        var (pw, ph) = OnnxUpscaleService.PaddedSize(w, h);
        Assert.Equal(expectedW, pw);
        Assert.Equal(expectedH, ph);
    }

    [Fact]
    public void RgbToTensor_separa_canais_normaliza_e_zera_padding()
    {
        // 2x2: vermelho, verde / azul, branco — padding 8x8 com zeros
        var rgb = new byte[]
        {
            255, 0, 0,   0, 255, 0,
            0, 0, 255,   255, 255, 255,
        };
        var tensor = OnnxUpscaleService.RgbToTensor(rgb, 2, 2, 8, 8);
        Assert.Equal(3 * 8 * 8, tensor.Length);

        var plane = 8 * 8;
        Assert.Equal(1f, tensor[0]);               // R(0,0) vermelho
        Assert.Equal(0f, tensor[1]);               // R(1,0) verde → R=0
        Assert.Equal(0f, tensor[8]);               // R(0,1) azul → R=0
        Assert.Equal(0f, tensor[plane]);           // G(0,0)
        Assert.Equal(1f, tensor[plane + 1]);       // G(1,0) verde
        Assert.Equal(1f, tensor[2 * plane + 8]);   // B(0,1) azul
        Assert.Equal(1f, tensor[2 * plane + 9]);   // B(1,1) branco
        Assert.Equal(0f, tensor[^1]);              // canto do padding (7,7)
    }

    [Fact]
    public void TensorToRgb_clampa_e_corta_o_padding()
    {
        const int pw = 8, ph = 8;
        var tensor = new float[3L * ph * 2 * pw * 2]; // saída 16x16
        var plane = (long)ph * 2 * pw * 2;
        Array.Fill(tensor, 0f);
        // R estoura pra cima (1.5 → 255), G pra baixo (-0.5 → 0), B fica no meio (0.5 → 128)
        for (var i = 0; i < plane; i++)
        {
            tensor[i] = 1.5f;
            tensor[plane + i] = -0.5f;
            tensor[2 * plane + i] = 0.5f;
        }

        var rgb = new byte[10 * 10 * 3]; // recorte 10x10 dentro dos 16x16
        OnnxUpscaleService.TensorToRgb(tensor, pw, ph, 10, 10, rgb);

        Assert.Equal(255, rgb[0]);   // R clampeado
        Assert.Equal(0, rgb[1]);     // G clampeado
        Assert.Equal(128, rgb[2]);   // B arredondado
        Assert.Equal(255, rgb[^3]);  // último pixel do recorte
    }

    [Fact]
    public void Extract_args_usam_rawvideo_cfr_sem_progress()
    {
        var args = UpscaleService.BuildRawExtractArgs("origem.mkv", 30, 720, "23.976");
        var ss = args.ToList().IndexOf("-ss");
        var i = args.ToList().IndexOf("-i");
        Assert.True(ss > 0 && ss < i, "-ss precisa vir antes de -i");
        Assert.Contains("rawvideo", args);
        Assert.Contains("cfr", args);
        Assert.Contains("pipe:1", args);
        Assert.Contains("rgb24", args);
        Assert.Contains("-frames:v", args);         // corte por frames (desync: -t quantizava)
        Assert.Contains("720", args);
        Assert.Contains("23.976", args);            // -r usa a taxa exata
        Assert.DoesNotContain("-progress", args);   // a stdout é vídeo cru — texto corromperia
        Assert.DoesNotContain(args, a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Assemble_args_usam_video_size_codigo_por_modo_e_saida_por_ultimo()
    {
        var lossless = UpscaleService.BuildRawAssembleArgs(1704, 960, "24000/1001", "seg.mkv", true, 1080);
        Assert.Contains("1704x960", lossless);
        Assert.Contains("pipe:0", lossless);
        Assert.Contains("24000/1001", lossless);    // -framerate com a fração exata do probe
        Assert.Contains("-qp", lossless);
        Assert.Contains("0", lossless);
        Assert.Contains("scale=-2:1080:flags=lanczos", lossless);
        Assert.Equal("seg.mkv", lossless[^1]);

        var final = UpscaleService.BuildRawAssembleArgs(640, 480, "24/1", "fim.mkv", false, 0);
        Assert.Contains("-crf", final);
        Assert.Contains("14", final);
        Assert.DoesNotContain("-vf", final);       // alvo 0 pula o lanczos
        Assert.DoesNotContain("-qp", final);
        Assert.Equal("fim.mkv", final[^1]);
    }

    [Fact]
    public async Task UpscalePartOnnxAsync_exige_dimensoes_e_modelos()
    {
        var service = new UpscaleService("ffmpeg-inexistente");

        var reqSemLargura = new UpscalePartRequest(
            "a.mkv", 0, 1, Path.GetTempPath(), "out.mkv",
            SourceHeight: 480, SourceFps: 24, TargetHeight: 1080,
            ModelCode: "onnx", UpscalerExePath: "",
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false);
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpscalePartOnnxAsync(reqSemLargura, CancellationToken.None, null));
        Assert.Contains("dimens", ex1.Message);

        var reqSemModelos = new UpscalePartRequest(
            "a.mkv", 0, 1, Path.GetTempPath(), "out.mkv",
            SourceHeight: 480, SourceFps: 24, TargetHeight: 1080,
            ModelCode: "onnx", UpscalerExePath: "",
            LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
            SourceWidth: 852, OnnxModelsDir: null);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpscalePartOnnxAsync(reqSemModelos, CancellationToken.None, null));
        Assert.Contains("models-onnx", ex2.Message);
    }

    [Fact]
    public void DxgiGpuProbe_enumera_ou_cai_para_nomes()
    {
        var viaFactory = DxgiGpuProbe.Enumerate();
        if (viaFactory.Count > 0)
        {
            // caminho normal: ordinais DXGI confiáveis (= device ids DML)
            Assert.True(DxgiGpuProbe.FactoryAvailable);
            Assert.All(viaFactory, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
            Assert.Equal(viaFactory.Count, viaFactory.Select(a => a.Index).Distinct().Count());
        }
        else
        {
            // factory quebrada (builds Insider): fallback de nomes SEM ordinais
            Assert.False(DxgiGpuProbe.FactoryAvailable);
            var fallback = DxgiGpuProbe.AdapterNamesFallback();
            Assert.NotEmpty(fallback); // ambiente de teste sempre tem GPU (iGPU conta)
            Assert.All(fallback, a => Assert.Equal(-1, a.Index));
        }

        Assert.False(string.IsNullOrWhiteSpace(DxgiGpuProbe.Describe()));
    }

    [Fact]
    public void BenchmarkSession_mediu_fps_positivo_no_device_0()
    {
        Assert.True(_modelsDir is not null, "tools\\models-onnx não encontrado (teste precisa dos modelos)");
        var model = OnnxUpscaleService.PickModelFile(_modelsDir!, 480);
        var fps = OnnxUpscaleService.CachedBenchmark(model, 0, 320, 240);
        Assert.True(fps > 0, $"benchmark devolveu {fps}");
    }

    private static string? _modelsDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "models-onnx");
                if (Directory.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
