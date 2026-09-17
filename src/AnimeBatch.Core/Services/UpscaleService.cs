using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Plano de upscale para um trecho: NeedsModel=false quando o alvo é menor ou igual à origem
/// (só redimensiona no remontar); ModelScale é o fator inteiro do modelo ncnn (2/3/4).
/// </summary>
public record UpscalePlan(bool NeedsModel, int ModelScale, int TargetHeight);

/// <summary>Parâmetros de uma etapa de upscale (por parte no modo WithEncode ou o arquivo inteiro no modo Only).</summary>
public record UpscalePartRequest(
    string SourcePath,
    double StartSeconds,
    double TotalSeconds,
    string WorkDir,
    string OutputPath,
    int SourceHeight,
    double SourceFps,
    int TargetHeight,
    string ModelCode,
    string UpscalerExePath,
    bool LosslessIntermediate,
    bool CopyAudio,
    bool KeepChapters,
    bool CopySubtitles,
    /// <summary>Índices Vulkan das GPUs de upscaling (setting "upscale.gpus"); null/[0] = só a principal.
    /// Ex.: [0, 2] roda um worker em cada placa; [0, 2, 0] dá dois workers à placa 0.
    /// No motor ONNX os índices são DXGI/DirectML (ordem do DxgiGpuProbe, não a Vulkan).</summary>
    IReadOnlyList<int>? GpuIds = null,
    /// <summary>Largura da origem — obrigatória no motor ONNX (o pipe cru precisa das dimensões exatas).</summary>
    int SourceWidth = 0,
    /// <summary>Pasta tools\models-onnx com os modelos AnimeJaNai (motor ONNX).</summary>
    string? OnnxModelsDir = null);

/// <summary>
/// Estágio de upscaling (M4): extrai frames do trecho com ffmpeg (PNG), amplia com
/// realcugan/realesrgan via Vulkan e remonta o vídeo. Processa em CHUNKS de 30s para o
/// pico de disco ficar em ~2 GB (PNG de episódio inteiro passaria de 40 GB).
///
/// Composição: cada chunk vira um segmento de vídeo SÓ-VÍDEO (x264 qp0 lossless no modo
/// WithEncode, CRF 14 no modo Only); os segmentos são emendados com o concat demuxer
/// (-c copy) e no mux final entram o áudio (e no Only, legendas/anexos/capítulos) da origem.
/// No modo WithEncode o produto é um intermediário que segue pro EncodeService normal;
/// no Only é o arquivo final.
/// </summary>
public class UpscaleService
{
    /// <summary>Duração de cada chunk (segundos de vídeo). Pico de disco ≈ 2 chunks de PNG.</summary>
    public const int ChunkSeconds = 30;

    private readonly string _ffmpeg;

    public UpscaleService(string ffmpegPath)
    {
        _ffmpeg = ffmpegPath;
    }

    /// <summary>
    /// Escolhe o fator do modelo: o menor inteiro ≥ ampliação necessária, limitado a 4
    /// (realesr-animevideov3 e realcugan suportam 2/3/4; a sobra — ou falta — é ajustada
    /// com lanczos no remontar para a altura exata).
    /// NOTA: já testamos o "menor inteiro que chega a ~80% do alvo" (480p→1080p com 2x+12,5%
    /// de lanczos) e foi MAIS LENTO que 3x na prática (23 vs 31fps) — revertido. Os frames
    /// intermediários ficam maiores que o alvo; o arquivo final sai na resolução exata.
    /// </summary>
    public static UpscalePlan SelectPlan(int sourceHeight, int targetHeight)
    {
        if (targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetHeight));
        if (sourceHeight <= 0 || targetHeight <= sourceHeight)
            return new UpscalePlan(false, 0, targetHeight);

        var factor = Math.Ceiling(targetHeight / (double)sourceHeight);
        var scale = (int)Math.Clamp(factor, 2, 4);
        return new UpscalePlan(true, scale, targetHeight);
    }

    /// <summary>Nível de denoise do realcugan: 1x/2x só existem no modelo 2x; em 3x/4x o único denoise é o 3x.</summary>
    public static int CuganNoiseLevel(int scale) => scale switch
    {
        2 => 1,
        _ => 3,
    };

    /// <summary>Pasta de modelos ao lado do executável escolhido pelo ToolsLocator.</summary>
    public static string ModelDirPath(string upscalerExePath, string modelCode)
    {
        var dir = Path.GetDirectoryName(upscalerExePath)
                  ?? throw new InvalidOperationException("Pasta do executável de upscale inválida.");
        return Path.Combine(dir, modelCode == "realesrgan" ? "models" : "models-se");
    }

    /// <summary>
    /// Extrai os frames de um chunk em PNG (%08d.png). IMPORTANTE: -fps_mode cfr força taxa
    /// CONSTANTE — fontes VFR (comuns em rips de anime) têm timestamps irregulares, e o
    /// remontar a taxa fixa deslizava contra o áudio progressivamente. O CFR reamostra no
    /// tempo (duplica/descarta frames), então cada chunk dura exatamente o trecho de origem.
    /// -progress pipe:1 alimenta o rodapé durante a extração.
    /// </summary>
    public static string[] BuildExtractArgs(string sourcePath, double startSeconds, double durationSeconds, string framesDir, double fps)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        if (startSeconds > 0)
            args.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", sourcePath]);
        if (durationSeconds > 0)
            args.AddRange(["-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(
        [
            "-map", "0:v:0",
            "-fps_mode", "cfr",
            "-r", fps.ToString("0.####", CultureInfo.InvariantCulture),
            "-start_number", "0",
            "-pix_fmt", "rgb24",
            "-progress", "pipe:1", "-nostats",
            Path.Combine(framesDir, "%08d.png"),
        ]);
        return [.. args];
    }

    /// <summary>Argumentos do realcugan/realesrgan em modo pasta (mantém os nomes %08d.png).
    /// gpuId: índice VULKAN da placa (não é o índice CUDA do nvidia-smi!) — 0=5060 Ti, 2=3060 nesta máquina.
    /// -j 1:2:8: o gargalo real é a COMPRESSÃO dos PNGs de saída (single-thread ~1,4s por frame
    /// de 2556x1440); 8 threads de save ~1,7x mais rápido.</summary>
    public static string[] BuildUpscaleArgs(string upscalerExePath, string modelCode, string inDir, string outDir, int scale, int gpuId)
    {
        var args = new List<string>
        {
            upscalerExePath,
            "-i", inDir,
            "-o", outDir,
            "-s", scale.ToString(CultureInfo.InvariantCulture),
        };

        if (modelCode == "realesrgan")
        {
            args.AddRange(["-n", "realesr-animevideov3", "-m", ModelDirPath(upscalerExePath, modelCode)]);
        }
        else
        {
            args.AddRange(["-n", CuganNoiseLevel(scale).ToString(CultureInfo.InvariantCulture), "-m", ModelDirPath(upscalerExePath, modelCode)]);
        }

        args.AddRange(["-g", gpuId.ToString(CultureInfo.InvariantCulture), "-j", "1:2:8", "-f", "png"]);
        return [.. args];
    }

    /// <summary>
    /// Remonta os frames ampliados de UM CHUNK em vídeo (só vídeo; áudio/legendas entram
    /// no mux final). Saída SEMPRE por último (opções valem para o arquivo seguinte no
    /// ffmpeg). Lossless: x264 qp0 (intermediário p/ encode); caso contrário CRF 14 (final).
    /// targetHeight 0 pula o filtro de escala (altura do modelo já é o alvo).
    /// </summary>
    public static string[] BuildAssembleArgs(
        string framesDir, double fps, string sourcePath, string outputPath,
        bool losslessIntermediate, bool copyAudio, bool keepChapters, bool copySubtitles,
        int targetHeight, double? durationSeconds)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-framerate", fps.ToString("0.####", CultureInfo.InvariantCulture),
            "-i", Path.Combine(framesDir, "%08d.png"),
            "-i", sourcePath,
            "-map", "0:v:0",
        };

        if (copyAudio)
            args.AddRange(["-map", "1:a:0?"]);
        if (copySubtitles)
            args.AddRange(["-map", "1:s?", "-map", "1:t?"]);

        // -t como opção de SAÍDA: sem ele o áudio (mapeado da origem inteira) estica a
        // duração do arquivo — validado: intermediário de 3s saía com 23min de áudio.
        if (durationSeconds is { } dur)
            args.AddRange(["-t", dur.ToString("0.###", CultureInfo.InvariantCulture)]);

        if (targetHeight > 0)
            args.AddRange(["-vf", $"scale=-2:{targetHeight}:flags=lanczos"]);

        if (!keepChapters)
            args.AddRange(["-map_chapters", "-1"]);

        // saída por último: -qp 0 = lossless p/ intermediário; CRF 14 = final visualmente transparente
        args.AddRange(losslessIntermediate
            ? ["-c:v", "libx264", "-qp", "0", "-preset", "veryfast", "-pix_fmt", "yuv420p"]
            : ["-c:v", "libx264", "-crf", "14", "-preset", "veryfast", "-pix_fmt", "yuv420p"]);

        if (copyAudio)
            args.AddRange(["-c:a", "copy"]);
        else
            args.AddRange(["-c:a", "aac", "-b:a", "160k"]);

        if (copySubtitles)
            args.AddRange(["-c:s", "copy", "-c:t", "copy"]);

        args.Add(outputPath);
        return [.. args];
    }

    /// <summary>Emenda os segmentos dos chunks com o concat demuxer (-c copy, sem re-encode).</summary>
    public static string[] BuildConcatArgs(string listFile, string concatOutput) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-f", "concat", "-safe", "0",
        "-i", listFile,
        "-c", "copy",
        concatOutput,
    ];

    /// <summary>
    /// Extração de um chunk para STDOUT CRU (motor ONNX): rawvideo rgb24 em vez de PNG —
    /// elimina o gargalo de compressão do pipeline ncnn. -fps_mode cfr mantém o sync em
    /// fontes VFR (mesma regra do BuildExtractArgs). SEM -progress: a stdout é o vídeo;
    /// o progresso conta por bytes de frame lidos.
    /// </summary>
    public static string[] BuildRawExtractArgs(string sourcePath, double startSeconds, double durationSeconds, double fps)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        if (startSeconds > 0)
            args.AddRange(["-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", sourcePath]);
        if (durationSeconds > 0)
            args.AddRange(["-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(
        [
            "-map", "0:v:0",
            "-fps_mode", "cfr",
            "-r", fps.ToString("0.####", CultureInfo.InvariantCulture),
            "-f", "rawvideo",
            "-pix_fmt", "rgb24",
            "pipe:1",
        ]);
        return [.. args];
    }

    /// <summary>
    /// Remonta um chunk do motor ONNX lendo o STDOUT CRU do pipe (rawvideo rgb24 com as
    /// dimensões FINAIS após os passes do modelo). Sem -t: o EOF do pipe fecha o segmento
    /// com o número exato de frames (e áudio não entra aqui — é do mux final).
    /// </summary>
    public static string[] BuildRawAssembleArgs(int width, int height, double fps, string outputPath, bool losslessIntermediate, int targetHeight)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo",
            "-pixel_format", "rgb24",
            "-video_size", $"{width}x{height}",
            "-framerate", fps.ToString("0.####", CultureInfo.InvariantCulture),
            "-i", "pipe:0",
        };

        if (targetHeight > 0)
            args.AddRange(["-vf", $"scale=-2:{targetHeight}:flags=lanczos"]);

        args.AddRange(["-map_chapters", "-1"]);

        args.AddRange(losslessIntermediate
            ? ["-c:v", "libx264", "-qp", "0", "-preset", "veryfast", "-pix_fmt", "yuv420p"]
            : ["-c:v", "libx264", "-crf", "14", "-preset", "veryfast", "-pix_fmt", "yuv420p"]);

        args.Add(outputPath);
        return [.. args];
    }

    /// <summary>
    /// Mux final: cola o vídeo emendado com o áudio (e, no modo Only, legendas/anexos e
    /// capítulos) da origem. Vídeo em -c copy; áudio copiado (Only) ou AAC 160k (intermediário).
    /// </summary>
    public static string[] BuildFinalMuxArgs(
        string concatPath, string sourcePath, string outputPath,
        double? startSeconds, double? durationSeconds,
        bool copyAudio, bool keepChapters, bool copySubtitles)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-i", concatPath,
        };

        if (startSeconds > 0)
            args.AddRange(["-ss", startSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", sourcePath, "-map", "0:v:0", "-map", "1:a:0?"]);

        if (copySubtitles)
            args.AddRange(["-map", "1:s?", "-map", "1:t?"]);

        if (durationSeconds is { } dur)
            args.AddRange(["-t", dur.ToString("0.###", CultureInfo.InvariantCulture)]);

        args.AddRange(["-c:v", "copy"]);
        args.AddRange(copyAudio ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "160k"]);

        if (copySubtitles)
            args.AddRange(["-c:s", "copy", "-c:t", "copy"]);

        // Partes NUNCA carregam os capítulos do original (regra do app); no modo Only preserva
        if (!keepChapters)
            args.AddRange(["-map_chapters", "-1"]);

        args.Add(outputPath);
        return [.. args];
    }

    /// <summary>Distribuição automática de workers pelas GPUs detectadas:
    /// nenhuma → duas instâncias na principal (uma só deixa a GPU ~60% ocupada);
    /// uma → idem; várias → um worker em cada + um extra na PRIMEIRA (a principal,
    /// mais rápida — sem isso ela perde throughput quando a segunda placa entra).</summary>
    public static IReadOnlyList<int> AutoWorkerGpus(IReadOnlyList<int> nvidiaIndices)
    {
        if (nvidiaIndices.Count == 0)
            return [0, 0];
        if (nvidiaIndices.Count == 1)
            return [nvidiaIndices[0], nvidiaIndices[0]];
        var plan = nvidiaIndices.ToList();
        plan.Add(nvidiaIndices[0]);
        return plan;
    }

    /// <summary>Lista do concat demuxer: uma linha "file '...'" por segmento (apóstrofos escapados).</summary>
    public static string BuildConcatListContent(IReadOnlyList<string> segmentPaths) =>
        string.Join('\n', segmentPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'"));

    /// <summary>
    /// Executa a etapa completa do trecho em chunks: extrai → amplia → remonta → concatena →
    /// mux final. progress recebe fração ponderada (extração 20%, upscale 75%, remontar 5%)
    /// em segundos de vídeo, no mesmo EncodeProgress que o encode usa — o rodapé não muda.
    /// </summary>
    public async Task UpscalePartAsync(UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress>? progress)
    {
        Directory.CreateDirectory(req.WorkDir);
        var segmentsDir = Path.Combine(req.WorkDir, "segments");
        CleanDir(segmentsDir);
        Directory.CreateDirectory(segmentsDir);

        // ffprobe pode devolver fps 0 em arquivos exóticos — sem framerate o remontar não abre
        var fps = req.SourceFps > 0 ? req.SourceFps : 23.976;
        var total = Math.Max(0.001, req.TotalSeconds);
        var chunkCount = Math.Max(1, (int)Math.Ceiling(total / ChunkSeconds));
        var plan = SelectPlan(req.SourceHeight, req.TargetHeight);

        if (plan.NeedsModel && !File.Exists(req.UpscalerExePath))
            throw new InvalidOperationException(
                $"Ferramenta de upscaling ausente: {Path.GetFileName(req.UpscalerExePath)} (pasta tools\\).");

        var sync = new object();      // protege os contadores globais (workers reportam em paralelo)
        var extractDone = 0.0;        // segundos de vídeo já extraídos (p/ fração 20%)
        var framesUpscaled = 0;       // frames já ampliados (p/ fração 75%)
        var upscaleWatch = Stopwatch.StartNew();
        var assembled = 0;            // chunks já remontados (p/ fração 5%)

        void Report(double fraction, double fpsNow, double speedNow) =>
            progress?.Report(new EncodeProgress(fpsNow, speedNow, Math.Clamp(fraction, 0, 1) * total));

        try
        {
            // Workers = GPUs configuradas (mínimo 2: mesmo com uma placa, duas instâncias
            // ncnn empurram a GPU de ~60% para ~90% — uma só fica esperando os PNGs).
            // Distribuição DINÂMICA de chunks: a placa mais rápida pega mais chunks sozinha.
            // Sem configuração manual: detecta as placas NVIDIA pelo próprio upscaler e
            // dá um worker extra à principal (com 1 worker por placa a 5060 perde throughput)
            if (req.GpuIds is not { Count: > 0 })
                await VulkanGpuProbe.EnsureDetectedAsync(req.UpscalerExePath, ct).ConfigureAwait(false);
            var gpus = req.GpuIds is { Count: > 0 }
                ? req.GpuIds
                : AutoWorkerGpus(VulkanGpuProbe.NvidiaIndices());
            var workerCount = Math.Clamp(Math.Max(2, gpus.Count), 1, chunkCount);
            var nextChunk = -1;
            var segmentPaths = new string[chunkCount];

            using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task Worker(int workerId)
            {
                while (true)
                {
                    var chunk = Interlocked.Increment(ref nextChunk);
                    if (chunk >= chunkCount)
                        return;
                    stageCts.Token.ThrowIfCancellationRequested();

                    var chunkStart = req.StartSeconds + chunk * (double)ChunkSeconds;
                    var chunkDuration = Math.Min(ChunkSeconds, total - chunk * (double)ChunkSeconds);
                    var framesSrc = Path.Combine(segmentsDir, $"c{chunk:000}_src");
                    var framesOut = Path.Combine(segmentsDir, $"c{chunk:000}_out");
                    CleanDir(framesSrc);
                    CleanDir(framesOut);
                    // o image2 do ffmpeg NÃO cria a pasta de saída — sem isso a extração morre no
                    // primeiro frame ("Could not open file … frames_src\00000000.png")
                    Directory.CreateDirectory(framesSrc);
                    Directory.CreateDirectory(framesOut);

                    try
                    {
                    // ---- extração (peso 20%), CFR para manter sync com o áudio em fontes VFR ----
                    var extractFps = 0.0;
                    var extractSpeed = 0.0;
                    await RunProcessAsync(_ffmpeg,
                        BuildExtractArgs(req.SourcePath, chunkStart, chunkDuration, framesSrc, fps), stageCts.Token,
                            line =>
                            {
                                if (line.StartsWith("fps=", StringComparison.Ordinal) &&
                                    double.TryParse(line[4..].TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                                    extractFps = f;
                                else if (line.StartsWith("speed=", StringComparison.Ordinal) &&
                                         double.TryParse(line[6..].TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp))
                                    extractSpeed = sp;
                                else if (EncodeService.TryParseOutTimeSeconds(line, out var seconds))
                                {
                                    double done;
                                    lock (sync) { done = extractDone; }
                                    Report((done + Math.Min(seconds, chunkDuration)) / total * 0.2, extractFps, extractSpeed);
                                }
                            }).ConfigureAwait(false);
                        lock (sync) { extractDone += chunkDuration; }

                        var count = Directory.GetFiles(framesSrc, "*.png").Length;
                        if (count == 0)
                            throw new InvalidOperationException($"Extração do chunk {chunk + 1}/{chunkCount} não gerou nenhum PNG.");

                        // ---- upscale (peso 75%) na GPU deste worker ----
                        var framesForAssemble = framesSrc;
                        if (plan.NeedsModel)
                        {
                            var gpu = gpus[workerId % gpus.Count];
                            var lastProduced = 0;
                            await RunUpscalerAsync(
                                BuildUpscaleArgs(req.UpscalerExePath, req.ModelCode, framesSrc, framesOut, plan.ModelScale, gpu),
                                framesOut, count, stageCts.Token, p =>
                                {
                                    var delta = p - lastProduced;
                                    lastProduced = p;
                                    if (delta <= 0)
                                        return;
                                    int totalUp;
                                    lock (sync)
                                    {
                                        framesUpscaled += delta;
                                        totalUp = framesUpscaled;
                                    }
                                    // fps médio desde o início do upscale — estável no rodapé
                                    var avgFps = upscaleWatch.Elapsed.TotalSeconds > 0.5
                                        ? totalUp / upscaleWatch.Elapsed.TotalSeconds
                                        : 0;
                                    double done;
                                    lock (sync) { done = extractDone; }
                                    var frac = (done / total * 0.2) +
                                               (fps > 0 ? Math.Min(1.0, totalUp / (total * fps)) : 0) * 0.75;
                                    Report(frac, avgFps, fps > 0 ? avgFps / fps : 0);
                                }).ConfigureAwait(false);
                            framesForAssemble = framesOut;
                        }

                        // ---- remontar o chunk (peso 5%, rápido: x264 veryfast só-vídeo) ----
                        var assembleHeight = plan.TargetHeight == req.SourceHeight * plan.ModelScale ? 0 : plan.TargetHeight;
                        var segmentPath = Path.Combine(segmentsDir, $"seg_{chunk:0000}.mkv");
                        await RunProcessAsync(_ffmpeg,
                            BuildAssembleArgs(framesForAssemble, fps, req.SourcePath, segmentPath,
                                req.LosslessIntermediate, copyAudio: false, keepChapters: false,
                                copySubtitles: false, assembleHeight, durationSeconds: null),
                            stageCts.Token, null).ConfigureAwait(false);
                        segmentPaths[chunk] = segmentPath;
                        int assembledNow;
                        lock (sync) { assembled++; assembledNow = assembled; }
                        Report((extractDone / total * 0.2) +
                               (fps > 0 ? Math.Min(1.0, framesUpscaled / (total * fps)) : 0) * 0.75 +
                               (assembledNow / (double)chunkCount) * 0.05, 0, 0);
                    }
                    finally
                    {
                        CleanDir(framesSrc);
                        CleanDir(framesOut);
                    }
                }
            }

            var workerTasks = Enumerable.Range(0, workerCount).Select(Worker).ToArray();
            try
            {
                await Task.WhenAll(workerTasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // falha real (não é pausa/stop): mata os workers vivos e devolve o erro do job
                stageCts.Cancel();
                try { await Task.WhenAll(workerTasks).ConfigureAwait(false); } catch { /* observa as demais */ }

                if (ex is OperationCanceledException)
                    throw new InvalidOperationException("Upscaling interrompido por falha em outro worker.", ex);
                throw;
            }

            // ---- concatenação dos segmentos (-c copy) + mux do áudio/legendas ----
            await ConcatAndMuxAsync(req, segmentPaths, segmentsDir, total, ct).ConfigureAwait(false);
            Report(1.0, 0, 0);
        }
        finally
        {
            CleanDir(segmentsDir);
        }
    }

    /// <summary>Finalização comum aos motores: emenda os segmentos (-c copy) e muxa o
    /// áudio/legendas/capítulos da origem no produto do estágio.</summary>
    private async Task ConcatAndMuxAsync(
        UpscalePartRequest req, string[] segmentPaths, string segmentsDir, double total, CancellationToken ct)
    {
        var segments = new List<string>(segmentPaths);
        var listFile = Path.Combine(segmentsDir, "concat.txt");
        var concatPath = Path.Combine(segmentsDir, "concat.mkv");
        await File.WriteAllTextAsync(listFile, BuildConcatListContent(segments), ct).ConfigureAwait(false);
        await RunProcessAsync(_ffmpeg, BuildConcatArgs(listFile, concatPath), ct, null).ConfigureAwait(false);

        var onlyMode = req.KeepChapters && req.CopyAudio;
        await RunProcessAsync(_ffmpeg,
            BuildFinalMuxArgs(concatPath, req.SourcePath, req.OutputPath,
                startSeconds: onlyMode ? null : req.StartSeconds,
                durationSeconds: onlyMode ? null : total,
                copyAudio: req.CopyAudio, keepChapters: req.KeepChapters, copySubtitles: req.CopySubtitles),
            ct, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Motor ONNX (AnimeJaNai via DirectML): mesma arquitetura de chunks/workers do caminho
    /// ncnn, mas sem PNG — cada worker liga DOIS ffmpeg por chunk (um despejando rawvideo
    /// rgb24 na stdout, outro montando o segmento x264 do stdin) e a inferência acontece
    /// IN-PROCESS entre os dois pipes. Elimina a compressão/descompressão de PNG que limitava
    /// o ncnn; o que sobra de custo é só a GPU e o x264.
    /// GPUs: req.GpuIds são índices DXGI/DirectML (ordem do DxgiGpuProbe); sem configuração,
    /// usa as NVIDIA (uma sessão por placa, worker extra na principal via AutoWorkerGpus).
    /// </summary>
    public async Task UpscalePartOnnxAsync(UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress>? progress)
    {
        if (req.SourceWidth <= 0 || req.SourceHeight <= 0)
            throw new InvalidOperationException("Sem largura/altura da origem — o motor ONNX precisa das dimensões exatas do vídeo.");
        if (string.IsNullOrWhiteSpace(req.OnnxModelsDir))
            throw new InvalidOperationException("Pasta de modelos ONNX não configurada (tools\\models-onnx).");

        Directory.CreateDirectory(req.WorkDir);
        var segmentsDir = Path.Combine(req.WorkDir, "segments");
        CleanDir(segmentsDir);
        Directory.CreateDirectory(segmentsDir);

        var fps = req.SourceFps > 0 ? req.SourceFps : 23.976;
        var total = Math.Max(0.001, req.TotalSeconds);
        var chunkCount = Math.Max(1, (int)Math.Ceiling(total / ChunkSeconds));
        var plan = SelectPlan(req.SourceHeight, req.TargetHeight);

        var modelFile = OnnxUpscaleService.PickModelFile(req.OnnxModelsDir!, req.SourceHeight);
        var passes = plan.NeedsModel
            ? OnnxUpscaleService.PlanPasses(modelFile, plan.ModelScale)
            : []; // alvo ≤ origem: sem modelo, o remontar só encolhe com lanczos

        // Geometria por passe: cada passe dobra w/h; o pad (múltiplo de 8) é por passe.
        var passGeoms = new List<(int W, int H, int PadW, int PadH)>();
        var (w, h) = (req.SourceWidth, req.SourceHeight);
        for (var i = 0; i < passes.Count; i++)
        {
            var (pw, ph) = OnnxUpscaleService.PaddedSize(w, h);
            passGeoms.Add((w, h, pw, ph));
            (w, h) = (w * 2, h * 2);
        }
        var (finalW, finalH) = (w, h);

        // Pula o lanczos quando a saída do modelo já é o alvo exato (2^passes * origem)
        var chainScale = 1 << passes.Count;
        var assembleHeight = plan.NeedsModel && plan.TargetHeight == req.SourceHeight * chainScale
            ? 0
            : plan.TargetHeight;

        // GPUs do pool. Manual: req.GpuIds valem como ids DirectML. Automático com factory
        // DXGI saudável: NVIDIA via ordinais DXGI. Fallback (factory quebrada, ex. Insider):
        // todos os ids DML existentes, podados por BENCHMARK — placa >3x mais lenta que a
        // melhor (iGPU) não recebe worker. Sessões criadas ANTES dos workers: compilar
        // shader DML custa segundos e a falha de driver precisa morrer com mensagem clara.
        IReadOnlyList<int> gpus;
        if (req.GpuIds is { Count: > 0 })
        {
            gpus = req.GpuIds;
        }
        else
        {
            var nvidia = DxgiGpuProbe.NvidiaIndices();
            gpus = nvidia.Count > 0
                ? AutoWorkerGpus(nvidia)
                : BenchmarkPruneGpus(modelFile, OnnxUpscaleService.ProbeDeviceIds(modelFile), req.SourceWidth, req.SourceHeight, ct);
        }
        // Sessões criadas ANTES dos workers, UMA POR WORKER (Run concorrente na mesma sessão
        // DirectML trava): compilar shader DML custa segundos e a falha de driver precisa
        // morrer com mensagem clara. O cache por (modelo, placa, slot do worker) faz a
        // compilação acontecer uma vez por aplicativo, não por capítulo.
        var workerCount = Math.Clamp(Math.Max(2, gpus.Count), 1, chunkCount);
        for (var workerId = 0; workerId < workerCount; workerId++)
            OnnxUpscaleService.GetSession(modelFile, gpus[workerId % gpus.Count], workerId);

        var sync = new object();
        var framesDone = 0;      // frames lidos + ampliados (peso 95% do progresso)
        var assembled = 0;       // chunks já remontados (peso 5%)
        var watch = Stopwatch.StartNew();
        var lastReportTicks = 0L;

        void Report(double fraction, double fpsNow, double speedNow) =>
            progress?.Report(new EncodeProgress(fpsNow, speedNow, Math.Clamp(fraction, 0, 1) * total));

        void ReportThrottled(double fraction, double fpsNow, double speedNow)
        {
            var now = Environment.TickCount64;
            if (now - lastReportTicks < 200)
                return;
            lastReportTicks = now;
            Report(fraction, fpsNow, speedNow);
        }

        try
        {
            var nextChunk = -1;
            var segmentPaths = new string[chunkCount];

            using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task Worker(int workerId)
            {
                var session = OnnxUpscaleService.GetSession(modelFile, gpus[workerId % gpus.Count], workerId);
                var inputName = session.InputMetadata.Keys.First();

                // buffers reutilizados por worker — zero alocação por frame (20-80 MB/frame
                // alocação nova destruiria o GC a 30+ fps)
                var srcBuf = new byte[(long)req.SourceWidth * req.SourceHeight * 3];
                var workBuf = new byte[(long)finalW * finalH * 3];
                var (lastPadW, lastPadH) = passGeoms.Count > 0
                    ? (passGeoms[^1].PadW, passGeoms[^1].PadH)
                    : OnnxUpscaleService.PaddedSize(req.SourceWidth, req.SourceHeight);
                var tensorBuf = new float[3L * lastPadW * lastPadH];

                while (true)
                {
                    var chunk = Interlocked.Increment(ref nextChunk);
                    if (chunk >= chunkCount)
                        return;
                    stageCts.Token.ThrowIfCancellationRequested();

                    var chunkStart = req.StartSeconds + chunk * (double)ChunkSeconds;
                    var chunkDuration = Math.Min(ChunkSeconds, total - chunk * (double)ChunkSeconds);
                    var segmentPath = Path.Combine(segmentsDir, $"seg_{chunk:0000}.mkv");

                    await ProcessChunkOnnxAsync(
                        req, session, inputName, passGeoms, finalW, finalH, fps, assembleHeight,
                        chunkStart, chunkDuration, segmentPath,
                        srcBuf, workBuf, tensorBuf,
                        frames =>
                        {
                            var now = Environment.TickCount64;
                            var elapsed = watch.Elapsed.TotalSeconds;
                            int done;
                            lock (sync)
                            {
                                framesDone += frames;
                                done = framesDone;
                            }
                            var avgFps = elapsed > 0.5 ? done / elapsed : 0;
                            ReportThrottled(
                                Math.Min(1.0, fps > 0 ? done / (total * fps) : 0) * 0.95,
                                avgFps,
                                fps > 0 ? avgFps / fps : 0);
                        },
                        stageCts.Token).ConfigureAwait(false);

                    segmentPaths[chunk] = segmentPath;
                    int assembledNow;
                    lock (sync) { assembled++; assembledNow = assembled; }
                    int done;
                    lock (sync) { done = framesDone; }
                    Report(
                        Math.Min(1.0, fps > 0 ? done / (total * fps) : 0) * 0.95 +
                        assembledNow / (double)chunkCount * 0.05, 0, 0);
                }
            }

            var workerTasks = Enumerable.Range(0, workerCount).Select(Worker).ToArray();
            try
            {
                await Task.WhenAll(workerTasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                stageCts.Cancel();
                try { await Task.WhenAll(workerTasks).ConfigureAwait(false); } catch { /* observa as demais */ }

                if (ex is OperationCanceledException)
                    throw new InvalidOperationException("Upscaling interrompido por falha em outro worker.", ex);
                throw;
            }

            await ConcatAndMuxAsync(req, segmentPaths, segmentsDir, total, ct).ConfigureAwait(false);
            Report(1.0, 0, 0);
        }
        finally
        {
            CleanDir(segmentsDir);
        }
    }

    /// <summary>Roda uma inferência de aquecimento/medição por candidato e mantém no pool só
    /// as placas com desempenho comparável à melhor (limiar 3x). A melhor NUNCA sai — kept
    /// nunca fica vazio. Benchmark em cache por (modelo, placa): roda uma vez por app.</summary>
    private static IReadOnlyList<int> BenchmarkPruneGpus(string modelFile, IReadOnlyList<int> candidates, int srcW, int srcH, CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [0, 0]; // sem nenhum DML: deixa o erro claro estourar no GetSession do worker

        var speeds = new Dictionary<int, double>();
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            speeds[id] = OnnxUpscaleService.CachedBenchmark(modelFile, id, srcW, srcH);
        }

        var best = speeds.Values.Max();
        var kept = speeds.Where(kv => kv.Value >= best / 3.0).Select(kv => kv.Key).ToList();
        return AutoWorkerGpus(kept);
    }

    /// <summary>Processa UM chunk no motor ONNX: ffmpeg extrai → inferência frame a frame →
    /// ffmpeg monta o segmento. Os dois processos e os buffers pertencem ao worker.</summary>
    private async Task ProcessChunkOnnxAsync(
        UpscalePartRequest req,
        InferenceSession session, string inputName,
        IReadOnlyList<(int W, int H, int PadW, int PadH)> passGeoms,
        int finalW, int finalH, double fps, int assembleHeight,
        double chunkStart, double chunkDuration, string segmentPath,
        byte[] srcBuf, byte[] workBuf, float[] tensorBuf,
        Action<int> onFrames, CancellationToken ct)
    {
        using var extract = StartProcess(_ffmpeg, BuildRawExtractArgs(req.SourcePath, chunkStart, chunkDuration, fps));
        using var assemble = StartProcess(_ffmpeg,
            BuildRawAssembleArgs(finalW, finalH, fps, segmentPath, req.LosslessIntermediate, assembleHeight),
            redirectStdin: true);

        var stderrExtract = extract.StandardError.ReadToEndAsync(CancellationToken.None);
        var stderrAssemble = assemble.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            var frameBytes = (int)((long)req.SourceWidth * req.SourceHeight * 3);
            var frames = 0;
            var extractStream = extract.StandardOutput.BaseStream;
            var assembleStream = assemble.StandardInput.BaseStream;
            while (await ReadExactAsync(extractStream, srcBuf, frameBytes, ct).ConfigureAwait(false))
            {
                var curBuf = srcBuf;
                var curW = req.SourceWidth;
                var curH = req.SourceHeight;

                for (var k = 0; k < passGeoms.Count; k++)
                {
                    var (iw, ih, pw, ph) = passGeoms[k];
                    OnnxUpscaleService.RgbToTensor(curBuf, iw, ih, pw, ph, tensorBuf);

                    using var input = OrtValue.CreateTensorValueFromMemory(
                        tensorBuf, new long[] { 1, 3, ph, pw });
                    using var runOpts = new RunOptions();
                    var inputs = new Dictionary<string, OrtValue> { [inputName] = input };
                    using IDisposableReadOnlyCollection<OrtValue> outputs =
                        session.Run(runOpts, inputs, session.OutputNames);

                    var outSpan = outputs[0].GetTensorMutableDataAsSpan<float>();
                    var (outW, outH) = (iw * 2, ih * 2);
                    if (outSpan.Length < 3L * pw * ph * 4)
                        throw new InvalidOperationException(
                            $"Saída do modelo menor que o esperado ({outSpan.Length} floats para {pw * 2}x{ph * 2}).");
                    OnnxUpscaleService.TensorToRgb(outSpan, pw, ph, outW, outH, workBuf);
                    curBuf = workBuf;
                    curW = outW;
                    curH = outH;
                }

                await assembleStream
                    .WriteAsync(curBuf.AsMemory(0, curW * curH * 3), ct).ConfigureAwait(false);
                onFrames(1);
                frames++;
            }

            assembleStream.Close(); // EOF do pipe: x264 finaliza o segmento

            await extract.WaitForExitAsync(ct).ConfigureAwait(false);
            await assemble.WaitForExitAsync(ct).ConfigureAwait(false);

            if (extract.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg (extração ONNX) falhou (código {extract.ExitCode}): {ProcessRunner.Truncate(await stderrExtract.ConfigureAwait(false), 500)}");
            if (assemble.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg (remontar ONNX) falhou (código {assemble.ExitCode}): {ProcessRunner.Truncate(await stderrAssemble.ConfigureAwait(false), 500)}");
            if (frames == 0)
                throw new InvalidOperationException("Extração ONNX não produziu nenhum frame no chunk.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            // IOException de pipe quebrado = o ffmpeg do outro lado morreu — traz o stderr dele
            var tail = "";
            try { tail = ProcessRunner.Truncate(await stderrAssemble.ConfigureAwait(false), 500); } catch { }
            throw new InvalidOperationException(
                $"Falha no chunk ONNX (frame pipe): {ex.Message} | ffmpeg: {tail}", ex);
        }
        finally
        {
            ProcessRunner.TryKill(extract);
            ProcessRunner.TryKill(assemble);
        }
    }

    /// <summary>Lê EXATAMENTE count bytes; false = fim do stream (mesmo no meio do frame).</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    private static Process StartProcess(string exe, string[] args, bool redirectStdin = false) =>
        ProcessRunner.Start(exe, args, redirectStdin);

    /// <summary>Roda o ncnn-vulkan num chunk; onFrames recebe frames prontos a cada poll
    /// (é por onde o progresso do upscale flui — o ncnn não emite progresso na stdout).</summary>
    private async Task RunUpscalerAsync(string[] args, string outDir, int expectedFrames, CancellationToken ct, Action<int>? onFrames)
    {
        // FileName TEM que ser o executável: ArgumentList é só "o resto da linha de comando".
        // Sem isso o Process.Start morre com "a file name has not been provided".
        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args.Skip(1))
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);

        try
        {
            var lastReported = -1;
            while (true)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                var produced = CountPngs(outDir);
                if (produced != lastReported)
                {
                    onFrames?.Invoke(produced);
                    lastReported = produced;
                }
                if (proc.HasExited)
                    break;
            }

            // última escrita de arquivo alcançar o disco antes da contagem final
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            onFrames?.Invoke(CountPngs(outDir));
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(proc);
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Upscaling falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(stderr, 800)}");

        var finalCount = CountPngs(outDir);
        if (finalCount < expectedFrames)
            throw new InvalidOperationException(
                $"Upscaling gerou {finalCount} de {expectedFrames} frames — processamento incompleto.");
    }

    private static int CountPngs(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.png").Length : 0;

    private async Task RunProcessAsync(string exe, string[] args, CancellationToken ct, Action<string>? onStdoutLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        var pumpTask = onStdoutLine is null
            ? Task.CompletedTask
            : PumpAsync(proc, onStdoutLine);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(proc);
            throw;
        }

        await pumpTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(stderr, 800)}");
    }

    private static async Task PumpAsync(Process proc, Action<string> onLine)
    {
        while (await proc.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            onLine(line);
    }

    private static void CleanDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort: frames órfãos não devem derrubar a fila
        }
    }
}
