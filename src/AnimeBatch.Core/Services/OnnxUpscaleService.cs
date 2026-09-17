using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;

namespace AnimeBatch.Core.Services;

/// <summary>Um passe do modelo: sempre 2x (todos os AnimeJaNai embarcados são 2x); 4x = dois passes.</summary>
public record OnnxModelPass(string ModelPath, int Scale);

/// <summary>
/// Motor de upscale ONNX Runtime (execution provider DirectML) com os modelos AnimeJaNai:
/// inferência IN-PROCESS na GPU, sem PNG e sem subprocesso — os frames chegam por pipe
/// (rawvideo rgb24) direto do ffmpeg, passam pelo modelo em float NCHW e voltam por pipe.
/// As sessões ficam em cache estático por (modelo, placa): compilar os shaders do DirectML
/// custa segundos e não pode acontecer a cada capítulo. Session.Run é thread-safe.
/// </summary>
public static class OnnxUpscaleService
{
    /// <summary>Código do motor na coluna UpscaleModel do job ("onnx").</summary>
    public const string MotorCode = "onnx";

    /// <summary>Entrada do modelo é re-cheiada com zero nas bordas (pad para múltiplo de 8 —
    /// pixel-unshuffle interno dos modelos pede dimensões divisíveis); a saída é cortada de volta.</summary>
    public const int PadMultiple = 8;

    /// <summary>Cache de sessões por (modelo, placa, SLOT). Duas Run() CONCORRENTES na MESMA
    /// sessão DirectML travam (testado no ORT 1.24.4) — workers na mesma placa precisam de
    /// sessões distintas. O slot é o índice do worker; compilação de shader acontece uma
    /// vez por aplicativo e as sessões são reaproveitadas entre capítulos.</summary>
    private static readonly ConcurrentDictionary<(string Model, int DeviceId, int Slot, bool Dml), Lazy<InferenceSession>> Sessions = new();

    /// <summary>Resultado do benchmark por (modelo, placa): frames/segundo da inferência.
    /// Medido uma vez por sessão do aplicativo — repetir por capítulo seria desperdício.</summary>
    private static readonly ConcurrentDictionary<(string Model, int DeviceId), double> BenchmarkCache = new();

    /// <summary>
    /// Escolhe o modelo pela ALTURA DA ORIGEM: SD Compact (≤480p, mais rápido e treinado pra SD)
    /// ou HD V3.1 Balanced (720p+; Balanced tem mais capacidade que o Performance e a GPU sobra).
    /// </summary>
    public static string PickModelFile(string modelsDir, int sourceHeight)
    {
        var sd = Path.Combine(modelsDir, "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong.onnx");
        var hd = Path.Combine(modelsDir, "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle.onnx");
        var hdPerf = Path.Combine(modelsDir, "2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle.onnx");

        var chosen = sourceHeight < 650 ? sd
            : File.Exists(hd) ? hd
            : File.Exists(hdPerf) ? hdPerf
            : sd;

        if (!File.Exists(chosen))
            throw new InvalidOperationException(
                $"Modelo ONNX ausente: {Path.GetFileName(chosen)} (esperado em {modelsDir}).");
        return chosen;
    }

    /// <summary>
    /// Passes do modelo: 2x/3x de plano = UM passe 2x (a sobra entra no lanczos do remontar —
    /// encadear um segundo passe quadruplicaria o custo de GPU por só ~12% de ganho real);
    /// 4x = dois passes 2x encadeados (960p→lanczos até 2160 degradaria demais).
    /// </summary>
    public static IReadOnlyList<OnnxModelPass> PlanPasses(string modelFile, int planScale) =>
        planScale >= 4
            ? [new OnnxModelPass(modelFile, 2), new OnnxModelPass(modelFile, 2)]
            : [new OnnxModelPass(modelFile, 2)];

    /// <summary>Dimensões re-cheiadas para múltiplo de PadMultiple.</summary>
    public static (int PadW, int PadH) PaddedSize(int w, int h) =>
        ((w + PadMultiple - 1) / PadMultiple * PadMultiple,
         (h + PadMultiple - 1) / PadMultiple * PadMultiple);

    /// <summary>
    /// Descobre quantos dispositivos DML existem criando sessões até falhar (ids são
    /// contíguos 0..N-1). Usado quando a factory DXGI não está disponível e não há como
    /// saber quais índices são NVIDIA. Slots próprios (80+id): sessões de descoberta
    /// nunca dividem objeto com workers ou benchmark — Run concorrente na mesma sessão trava.
    /// </summary>
    public static IReadOnlyList<int> ProbeDeviceIds(string modelPath)
    {
        var ids = new List<int>();
        for (var id = 0; id < 8; id++)
        {
            try
            {
                GetSession(modelPath, id, 80 + id);
                ids.Add(id);
            }
            catch
            {
                break; // primeiro id inexistente encerra
            }
        }
        return ids;
    }

    /// <summary>
    /// Mede frames/segundo de UMA sessão com o modelo real num tamanho próximo ao do vídeo.
    /// Roda 2 aquecimentos + 8 medições. Serve para PODAR placas lentas do pool (iGPU sai
    /// sozinha: >3x mais lenta que a melhor placa não recebe worker) sem depender de nome
    /// ou ordinal de adaptador.
    /// </summary>
    public static double BenchmarkSession(InferenceSession session, string inputName, int srcW, int srcH)
    {
        var (pw, ph) = PaddedSize(Math.Min(srcW, 1280), Math.Min(srcH, 720));
        var input = new float[3L * pw * ph];
        var inputs = new Dictionary<string, OrtValue>();
        var runOpts = new RunOptions();
        try
        {
            using var tensor = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, ph, pw });
            inputs[inputName] = tensor;

            for (var i = 0; i < 10; i++)
            {
                using var outputs = session.Run(runOpts, inputs, session.OutputNames);
                if (i < 2)
                    continue; // aquecimento (compilação/caching de shader)
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int rounds = 8;
            for (var i = 0; i < rounds; i++)
            {
                using var outputs = session.Run(runOpts, inputs, session.OutputNames);
            }
            sw.Stop();
            return rounds / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            runOpts.Dispose();
            inputs.Clear();
        }
    }

    /// <summary>Benchmark com cache por (modelo, placa). Slot próprio (90+id): o benchmark
    /// pode rodar em paralelo com workers usando a mesma placa — sessões não podem coincidir.</summary>
    public static double CachedBenchmark(string modelPath, int deviceId, int srcW, int srcH)
    {
        var session = GetSession(modelPath, deviceId, 90 + (deviceId % 8));
        return BenchmarkCache.GetOrAdd(
            (Path.GetFullPath(modelPath), deviceId),
            _ => BenchmarkSession(session, session.InputMetadata.Keys.First(), srcW, srcH));
    }

    /// <summary>Sessão DML em cache. slot = índice do worker (sessões distintas por worker —
    /// Run concorrente na mesma sessão trava); a compilação custa segundos e fica em cache.</summary>
    public static InferenceSession GetSession(string modelPath, int deviceId, int slot = 0, bool dml = true) =>
        Sessions.GetOrAdd(
            (Path.GetFullPath(modelPath), deviceId, slot, dml),
            _ => new Lazy<InferenceSession>(() => CreateSession(modelPath, deviceId, dml))).Value;

    /// <summary>Limpa o cache de sessões (testes de integração e troca de driver).</summary>
    public static void ResetSessions()
    {
        BenchmarkCache.Clear();
        foreach (var lazy in Sessions.Values)
            try { lazy.Value.Dispose(); } catch { /* sessão já morta */ }
        Sessions.Clear();
    }

    private static InferenceSession CreateSession(string modelPath, int deviceId, bool dml)
    {
        var so = new SessionOptions();
        try
        {
            so.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            if (dml)
                so.AppendExecutionProvider_DML(deviceId);
            so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            return new InferenceSession(modelPath, so);
        }
        catch (Exception ex)
        {
            so.Dispose();
            throw new InvalidOperationException(
                dml
                    ? $"Falha ao criar sessão DirectML na placa {deviceId} ({Path.GetFileName(modelPath)}): {ex.Message}"
                    : $"Falha ao criar sessão ONNX ({Path.GetFileName(modelPath)}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// RGB24 intercalado (frame cru do ffmpeg) → float NCHW 1x3xPadHxPadW normalizado 0..1,
    /// escrito no tensor fornecido (tamanho 3*padW*padH). O padding (linhas/colunas além de
    /// w,h) já nasce zero — o chamador passa o buffer zerado.
    /// </summary>
    public static void RgbToTensor(ReadOnlySpan<byte> rgb, int w, int h, int padW, int padH, Span<float> tensor)
    {
        if (tensor.Length < 3L * padW * padH)
            throw new ArgumentException("Tensor menor que 3xPadWxPadH.", nameof(tensor));
        tensor.Clear();
        unsafe
        {
            fixed (byte* src = rgb)
            fixed (float* dst = tensor)
            {
                var plane = (long)padW * padH;
                float* r = dst, g = dst + plane, b = dst + 2 * plane;
                for (var y = 0; y < h; y++)
                {
                    var row = src + (long)y * w * 3;
                    var dr = r + (long)y * padW;
                    var dg = g + (long)y * padW;
                    var db = b + (long)y * padW;
                    for (var x = 0; x < w; x++)
                    {
                        dr[x] = row[x * 3] / 255f;
                        dg[x] = row[x * 3 + 1] / 255f;
                        db[x] = row[x * 3 + 2] / 255f;
                    }
                }
            }
        }
    }

    /// <summary>Versão que aloca o tensor (atalho para testes).</summary>
    public static float[] RgbToTensor(ReadOnlySpan<byte> rgb, int w, int h, int padW, int padH)
    {
        var tensor = new float[3L * padW * padH];
        RgbToTensor(rgb, w, h, padW, padH, tensor);
        return tensor;
    }

    /// <summary>
    /// Saída do modelo (float NCHW 1x3x2PadHx2PadW, 0..1) → RGB24 intercalado CORTADO para
    /// cropW x cropH (a margem do padding é descartada). Valores são clampeados (o modelo
    /// pode estourar levemente o range).
    /// </summary>
    public static void TensorToRgb(ReadOnlySpan<float> tensor, int padW, int padH, int cropW, int cropH, Span<byte> rgb)
    {
        var outW = padW * 2;
        var outH = padH * 2;
        if (rgb.Length < (long)cropW * cropH * 3)
            throw new ArgumentException("Buffer RGB menor que o recorte pedido.", nameof(rgb));
        unsafe
        {
            fixed (float* src = tensor)
            fixed (byte* dst = rgb)
            {
                var plane = (long)outW * outH;
                float* r = src, g = src + plane, b = src + 2 * plane;
                for (var y = 0; y < cropH; y++)
                {
                    var row = dst + (long)y * cropW * 3;
                    var sr = r + (long)y * outW;
                    var sg = g + (long)y * outW;
                    var sb = b + (long)y * outW;
                    for (var x = 0; x < cropW; x++)
                    {
                        row[x * 3] = Clamp(sr[x]);
                        row[x * 3 + 1] = Clamp(sg[x]);
                        row[x * 3 + 2] = Clamp(sb[x]);
                    }
                }
            }
        }
    }

    private static byte Clamp(float v)
    {
        var i = (int)(v * 255f + 0.5f);
        return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
    }
}
