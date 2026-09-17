using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using System.Diagnostics;
using System.Globalization;

namespace AnimeBatch.App.Services;

/// <summary>Estatísticas do que está rodando agora (rodapé da aplicação).</summary>
public record QueueStats(
    double Fps,
    double Speed,
    double ConvertedMinutes,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string CurrentLabel);

/// <summary>
/// Executa a fila: pega o menor Order pendente, encoda cada parte com o codec do job,
/// junta com mkvmerge (capítulos cumulativos; Critical não vira capítulo), grava o
/// histórico e toca o som de conclusão por episódio. Um job por vez, em sequência.
///
/// Pausar: interrompe, job volta como Paused; Iniciar retoma (partes Done são puladas).
/// Parar: interrompe, job volta como Pending (a parte em curso recomeça do zero).
/// </summary>
internal class QueueRunner
{
    private CancellationTokenSource? _cts;
    private bool _pauseRequested;
    private bool _autoRemove;
    private readonly Stopwatch _stopwatch = new();
    private double _sessionConvertedSeconds;
    private double _jobDoneSeconds;
    private double _jobTotalSeconds;
    private string _currentLabel = "";

    public bool IsRunning { get; private set; }
    public QueueStats? LastStats { get; private set; }

    /// <summary>Fila mudou (job começou/concluiu/errou) — a tela deve recarregar.</summary>
    public event Action? Changed;

    /// <summary>Progresso do job em execução (jobId, 0–100).</summary>
    public event Action<int, double>? Progress;

    /// <summary>Estatísticas vivas para o rodapé.</summary>
    public event Action<QueueStats>? StatsUpdated;

    public async Task RunAsync()
    {
        if (IsRunning)
            return;

        IsRunning = true;
        _pauseRequested = false;
        _cts = new CancellationTokenSource();
        _sessionConvertedSeconds = 0;
        _stopwatch.Restart();

        // Iniciar também é "tentar de novo" e "continuar": Error e Paused voltam pra fila.
        await AppServices.Jobs.ResetErrorJobsAsync().ConfigureAwait(false);
        await AppServices.Jobs.ResetPausedJobsAsync().ConfigureAwait(false);
        // Job Running órfão = app morto no meio do processamento (kill/crash) — volta pra fila
        await AppServices.Jobs.ResetRunningJobsAsync().ConfigureAwait(false);

        // "Remover da fila ao ser convertido": lido uma vez por execução
        _autoRemove = await AppServices.Settings
            .GetAsync(SettingsRepository.QueueAutoRemove)
            .ConfigureAwait(false) == "true";
        // (GPUs de upscaling são lidas POR JOB — mudar a configuração vale já no próximo job,
        // sem precisar parar e iniciar a fila de novo)

        Changed?.Invoke();
        try
        {
            await ProcessQueueAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            IsRunning = false;
            _stopwatch.Stop();
            LastStats = null;
            Changed?.Invoke();
            StatsUpdated?.Invoke(new QueueStats(0, 0, _sessionConvertedSeconds / 60.0, _stopwatch.Elapsed, null, ""));
        }
    }

    /// <summary>Interrompe e deixa o job atual como Paused (retomável pelo Iniciar).</summary>
    public void Pause()
    {
        _pauseRequested = true;
        _cts?.Cancel();
    }

    /// <summary>Interrompe o processamento; o job atual volta a Pending (parte em curso recomeça).</summary>
    public void Stop()
    {
        _cts?.Cancel();
    }

    /// <summary>Publica as estatísticas atuais (mesmo antes do primeiro tick do ffmpeg).</summary>
    private void EmitStats()
    {
        LastStats = new QueueStats(
            0, 0,
            (_sessionConvertedSeconds + _jobDoneSeconds) / 60.0,
            _stopwatch.Elapsed,
            null,
            _currentLabel);
        StatsUpdated?.Invoke(LastStats);
    }

    /// <summary>Timeout de stall do encode (setting "queue.stallMinutes", em minutos;
    /// vazio/inválido = default de 10). Clamp 1–120: valor absurdo não pode desligar a
    /// proteção nem matar um encode saudável.</summary>
    private static TimeSpan ReadStallTimeout()
    {
        try
        {
            var raw = AppServices.Settings.GetAsync(SettingsRepository.StallMinutes)
                .GetAwaiter().GetResult();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
                return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 120));
        }
        catch
        {
            // leitura de setting não pode impedir a fila de rodar
        }
        return EncodeService.DefaultStallTimeout;
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var tools = AppServices.Tools;
        if (tools.FfmpegPath is null || tools.MkvMergePath is null || AppServices.Probe is null)
            throw new InvalidOperationException("Ferramentas essenciais ausentes (ffmpeg/mkvmerge/ffprobe).");

        var encode = new EncodeService(tools.FfmpegPath, ReadStallTimeout());
        var merge = new MergeService(tools.MkvMergePath);
        var upscale = new UpscaleService(tools.FfmpegPath);

        while (!ct.IsCancellationRequested)
        {
            var job = await AppServices.Jobs.PeekNextPendingAsync().ConfigureAwait(false);
            if (job is null)
                break;

            await AppServices.Jobs.SetStateAsync(job.Id, JobState.Running).ConfigureAwait(false);
            _currentLabel = job.SeriesName ?? Path.GetFileNameWithoutExtension(job.SourcePath);
            EmitStats();
            Changed?.Invoke();

            try
            {
                if (job.UpscaleMode == UpscaleMode.Only)
                    await ProcessOnlyUpscaleAsync(job, upscale, ct).ConfigureAwait(false);
                else
                    await ProcessJobAsync(job, encode, merge, upscale, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Pausar deixa o job Paused (retoma depois); Parar devolve a Pending (parte recomeça)
                var finalState = _pauseRequested ? JobState.Paused : JobState.Pending;
                _pauseRequested = false;
                await AppServices.Jobs.SetStateAsync(job.Id, finalState).ConfigureAwait(false);
                Changed?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                AppServices.LogCrash("QueueRunner", ex);
                await AppServices.Jobs.SetStateAsync(job.Id, JobState.Error, ShortError(ex)).ConfigureAwait(false);
                Changed?.Invoke();
                // segue pro próximo job da fila
            }
        }
    }

    /// <summary>Primeira linha do erro, enxuta o bastante pra caber na row da fila.</summary>
    private static string ShortError(Exception ex)
    {
        var msg = ex.Message.ReplaceLineEndings(" ");
        return msg.Length <= 200 ? msg : msg[..200] + "…";
    }

    /// <summary>Converte "0,2" em [0,2]; vazio/nulo → null (auto: detecta as NVIDIA pelo upscaler).</summary>
    private static IReadOnlyList<int>? ParseGpuList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var gpus = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var g) && g >= 0 ? g : -1)
            .Where(g => g >= 0)
            .Distinct()
            .ToList();
        return gpus.Count > 0 ? gpus : null;
    }

    /// <summary>Mensagem para a configuração explícita que não sobrou placa nenhuma
    /// (todos os workers em 0 na tela Hardware (GPU)) — melhor que cair no automático e
    /// usar exatamente a placa que o usuário excluiu.</summary>
    private static InvalidOperationException NoGpuEnabled() =>
        new("Hardware (GPU): todas as placas estão com 0 workers — nada seria processado. Escolha ao menos 1 worker em uma placa ou use 'Usar automático'.");

    /// <summary>GPUs do job, na precedência: (1) tela Hardware (GPU) — workers por placa por
    /// NOME, resolvido no espaço de índices do motor do job; (2) setting antiga "upscale.gpus"
    /// (índices crus); (3) null = automático do motor. Configuração presente é DECISÃO FINAL:
    /// pool vazio explícito derruba o job com erro claro (não cai no automático).</summary>
    private async Task<IReadOnlyList<int>?> ResolveUpscaleGpusAsync(string? modelCode, string upscalerExe)
    {
        var cards = HardwareGpuService.Parse(
            await AppServices.Settings.GetAsync(HardwareGpuService.SettingKey).ConfigureAwait(false));
        if (cards.Count > 0)
        {
            var resolved = modelCode == OnnxUpscaleService.MotorCode
                ? GpuSelector.ResolveOnnx(cards, AppServices.Tools.OnnxModelsDir)
                : await GpuSelector.ResolveNcnnAsync(cards, upscalerExe).ConfigureAwait(false);
            if (resolved is not null)
            {
                if (resolved.Count == 0)
                    throw NoGpuEnabled();
                return resolved;
            }
        }

        return ParseGpuList(await AppServices.Settings.GetAsync(SettingsRepository.UpscaleGpus).ConfigureAwait(false));
    }

    /// <summary>Escolhe o motor pelo código do job: "onnx" (AnimeJaNai in-process) ou os ncnn legados.</summary>
    private static Task RunUpscaleStageAsync(
        UpscaleService upscale, UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress> progress) =>
        req.ModelCode == OnnxUpscaleService.MotorCode
            ? upscale.UpscalePartOnnxAsync(req, ct, progress)
            : upscale.UpscalePartAsync(req, ct, progress);

    /// <summary>Pendência de encode montada depois do estágio de upscale (ou direto, sem upscale).</summary>
    private sealed record PendingEncode(
        JobItem Item, string OutPath, string? IntermediatePath,
        string SourcePath, EncodeService.JobItemRef Ref, string PassLogBase, double Duration);

    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly object _counterGate = new();

    /// <summary>Escrita de estado de item serializada — o encode paralelo dispara N trabalhadores
    /// e o SQLite não gosta de escritas concorrentes.</summary>
    private async Task SetItemGatedAsync(int itemId, JobItemState state, string? outputPath = null)
    {
        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await AppServices.Jobs.SetItemStateAsync(itemId, state, outputPath).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task ProcessJobAsync(Job job, EncodeService encode, MergeService merge, UpscaleService upscale, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var outputRoot = AppServices.GetOutputDirectory();
        var workDir = Path.Combine(outputRoot, "AnimeBatch", SafeFolder(baseName));
        Directory.CreateDirectory(workDir);

        // Dimensões/fps da origem guiam o plano de upscale (fator do modelo, escala final)
        var info = await AppServices.Probe!.ProbeAsync(job.SourcePath, ct).ConfigureAwait(false);
        var upscaledIntermediates = job.UpscaleMode == UpscaleMode.WithEncode;

        var items = job.Items.OrderBy(i => i.Order).ToList();
        _jobTotalSeconds = items.Sum(i => Math.Max(0.001, i.EndSeconds - i.StartSeconds));
        _jobDoneSeconds = 0;
        var codec = job.VideoCodec ?? "nvenc_av1_10bit";
        var cfg = await AppServices.EncodeConfigs.GetAsync(codec).ConfigureAwait(false);
        var isNvenc = codec.StartsWith("nvenc", StringComparison.Ordinal);
        var doneParts = new List<(JobItem Item, string Path)>();

        // Fase 1: upscale (uma parte por vez — o estágio já paraleliza dentro) e montagem
        // das pendências de encode. Fase 2: encode sequencial ou em paralelo por GPU.
        var pending = new List<PendingEncode>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var outPath = Path.Combine(workDir,
                $"{item.Order:00} - {ChapterService.SanitizeTitle(item.Title)} - {baseName}.mkv");
            var intermediatePath = Path.Combine(workDir, $"ups_{item.Order:00}.mkv");
            var itemDuration = Math.Max(0.001, item.EndSeconds - item.StartSeconds);

            // Retomada: partes concluídas com arquivo presente não são re-encodadas
            if (item.State == JobItemState.Done && File.Exists(outPath))
            {
                doneParts.Add((item, outPath));
                _jobDoneSeconds += itemDuration;
                continue;
            }

            // ---- Estágio de upscale (WithEncode): frames ampliados → intermediário lossless ----
            if (upscaledIntermediates && !File.Exists(intermediatePath))
            {
                await AppServices.Jobs.SetItemStateAsync(item.Id, JobItemState.Upcaling).ConfigureAwait(false);
                _currentLabel = $"{job.SeriesName ?? baseName} - {item.Order}/{items.Count} - {item.Title} · upscaling";
                EmitStats();

                var upsProgress = new Progress<EncodeProgress>(p =>
                    ProgressInternal(job.Id, p));
                try
                {
                    var req = new UpscalePartRequest(
                        job.SourcePath, item.StartSeconds, itemDuration,
                        Path.Combine(workDir, $"ups_frames_{item.Order:00}"), intermediatePath,
                        info.Height, info.Fps, job.UpscaleTargetHeight ?? 0,
                        job.UpscaleModel ?? "realcugan", UpscalerExe(job.UpscaleModel),
                        LosslessIntermediate: true, CopyAudio: false, KeepChapters: false, CopySubtitles: false,
                        GpuIds: await ResolveUpscaleGpusAsync(job.UpscaleModel, UpscalerExe(job.UpscaleModel)).ConfigureAwait(false),
                        SourceWidth: info.Width,
                        OnnxModelsDir: AppServices.Tools.OnnxModelsDir);
                    await RunUpscaleStageAsync(upscale, req, ct, upsProgress).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await AppServices.Jobs.SetItemStateAsync(item.Id, JobItemState.Pending).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    AppServices.LogCrash("UpscaleService", ex);
                    await AppServices.Jobs.SetItemStateAsync(item.Id, JobItemState.Error).ConfigureAwait(false);
                    throw; // marca o job como Error no chamador e segue a fila
                }
            }

            // Com upscale, o intermediário contém SÓ a parte → corte relativo começa em 0
            pending.Add(new PendingEncode(
                item, outPath, upscaledIntermediates ? intermediatePath : null,
                upscaledIntermediates ? intermediatePath : job.SourcePath,
                upscaledIntermediates
                    ? new EncodeService.JobItemRef(0, itemDuration, item.TargetKbps)
                    : new EncodeService.JobItemRef(item.StartSeconds, item.EndSeconds, item.TargetKbps),
                Path.Combine(workDir, $"pass_{item.Order:00}"),
                itemDuration));
        }

        // ---- Fase 2: encode das partes pendentes ----
        if (pending.Count > 0)
        {
            // Pool de encode NVENC: workers por placa da tela Hardware (GPU) no espaço
            // NVENC/nvidia-smi. Sem configuração (ou SVT no CPU): sequencial como antes;
            // configurado com todas as placas em 0 = erro claro (decisão final do usuário).
            var cardsJson = isNvenc
                ? await AppServices.Settings.GetAsync(HardwareGpuService.SettingKey).ConfigureAwait(false)
                : null;
            var pool = isNvenc ? NvencGpuProbe.ResolveWorkers(cardsJson) : null;
            if (pool is { Count: 0 })
                throw NoGpuEnabled();

            if (pool is { Count: > 0 })
                await EncodePendingParallelAsync(job, encode, cfg, pending, pool, ct).ConfigureAwait(false);
            else
                await EncodePendingSequentialAsync(job, encode, cfg, pending, ct).ConfigureAwait(false);

            foreach (var e in pending)
                doneParts.Add((e.Item, e.OutPath));
        }

        // Junção: capítulos cumulativos, uma entrada por parte não-crítica (no encode
        // paralelo a conclusão pode sair fora de ordem → ordena pelo Order)
        var finalPath = Path.Combine(outputRoot, $"{baseName}.mkv");
        var chaptersPath = Path.Combine(workDir, $"{baseName}_chapters.txt");
        var mergeParts = doneParts
            .OrderBy(p => p.Item.Order)
            .Select(e => (PartPath: e.Path, e.Item.Title, e.Item.StartSeconds, e.Item.EndSeconds, IsCritical: e.Item.Class == BitrateClass.Critical))
            .ToList();
        await merge.MergeAsync(mergeParts, finalPath, chaptersPath, ct).ConfigureAwait(false);

        // Histórico (tela de Séries → Arquivos Convertidos)
        var series = await AppServices.Series
            .FindByNormalizedNameAsync(ChapterService.CleanSeriesName(baseName))
            .ConfigureAwait(false);
        await AppServices.Conversions.AddAsync(new ConversionRecord
        {
            SeriesId = series?.Id,
            SeriesName = job.SeriesName ?? series?.Name ?? "",
            ConvertedAt = DateTime.Now,
            FileName = Path.GetFileName(finalPath),
            DurationSeconds = items.Sum(i => Math.Max(0, i.EndSeconds - i.StartSeconds)),
            SizeBytes = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0,
            OutputPath = finalPath,
        }).ConfigureAwait(false);

        await FinishJobAsync(job).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task EncodePendingSequentialAsync(
        Job job, EncodeService encode, CodecEncodeConfig cfg,
        IReadOnlyList<PendingEncode> pending, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var total = pending.Count;

        foreach (var e in pending)
        {
            ct.ThrowIfCancellationRequested();

            await SetItemGatedAsync(e.Item.Id, JobItemState.Encoding).ConfigureAwait(false);
            _currentLabel = $"{job.SeriesName ?? baseName} - {e.Item.Order}/{total} - {e.Item.Title}";
            EmitStats();

            var progress = new Progress<EncodeProgress>(p => ProgressInternal(job.Id, p));
            try
            {
                await encode.EncodePartAsync(
                    e.SourcePath, e.Ref, e.OutPath, e.PassLogBase, cfg, e.Item.TargetKbps, ct, progress).ConfigureAwait(false);

                await SetItemGatedAsync(e.Item.Id, JobItemState.Done, e.OutPath).ConfigureAwait(false);
                if (e.IntermediatePath is not null)
                    TryDelete(e.IntermediatePath);
            }
            catch (OperationCanceledException)
            {
                await SetItemGatedAsync(e.Item.Id, JobItemState.Pending).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                AppServices.LogCrash("EncodeService", ex);
                await SetItemGatedAsync(e.Item.Id, JobItemState.Error).ConfigureAwait(false);
                throw; // marca o job como Error no chamador e segue a fila
            }

            lock (_counterGate)
            {
                _jobDoneSeconds += e.Duration;
                _sessionConvertedSeconds += e.Duration;
            }
            Progress?.Invoke(job.Id, Math.Clamp(_jobDoneSeconds / _jobTotalSeconds * 100, 0, 100));
        }
    }

    /// <summary>
    /// Encode paralelo das partes: cada worker pega a próxima pendência e roda um ffmpeg
    /// apontado (-gpu N) a uma placa do pool (workers por placa = tela Hardware (GPU)).
    /// Uma parte que falha para de pegar novas mas deixa as em voo terminarem.
    /// </summary>
    private async Task EncodePendingParallelAsync(
        Job job, EncodeService encode, CodecEncodeConfig cfg,
        IReadOnlyList<PendingEncode> pending, IReadOnlyList<int> pool, CancellationToken ct)
    {
        var workerCount = Math.Min(pool.Count, pending.Count);
        var name = job.SeriesName ?? Path.GetFileNameWithoutExtension(job.SourcePath);
        _currentLabel = $"{name} - encode paralelo ({pending.Count} partes · {workerCount} workers)";
        EmitStats();

        var agg = new EncodeAggregator(workerCount, (outSum, fps, speed) => ProgressParallel(job.Id, outSum, fps, speed));
        var next = -1;
        var stopPickup = false;
        var workers = new List<Task>();
        for (var w = 0; w < workerCount; w++)
        {
            var slot = w;
            var gpu = pool[slot % pool.Count];
            workers.Add(Task.Run(async () =>
            {
                var progress = agg.Slot(slot);
                while (!stopPickup && !ct.IsCancellationRequested)
                {
                    var idx = Interlocked.Increment(ref next);
                    if (idx >= pending.Count)
                        return;
                    var e = pending[idx];
                    agg.Begin(slot, e.Duration);
                    try
                    {
                        await SetItemGatedAsync(e.Item.Id, JobItemState.Encoding).ConfigureAwait(false);
                        await encode.EncodePartAsync(
                            e.SourcePath, e.Ref, e.OutPath, e.PassLogBase, cfg, e.Item.TargetKbps, ct, progress, gpu).ConfigureAwait(false);

                        await SetItemGatedAsync(e.Item.Id, JobItemState.Done, e.OutPath).ConfigureAwait(false);
                        if (e.IntermediatePath is not null)
                            TryDelete(e.IntermediatePath);

                        lock (_counterGate)
                        {
                            _jobDoneSeconds += e.Duration;
                            _sessionConvertedSeconds += e.Duration;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        await SetItemGatedAsync(e.Item.Id, JobItemState.Pending).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppServices.LogCrash("EncodeService", ex);
                        await SetItemGatedAsync(e.Item.Id, JobItemState.Error).ConfigureAwait(false);
                        stopPickup = true; // esta parte falhou — não pegue outras
                        throw;
                    }
                    finally
                    {
                        agg.End(slot);
                    }
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // pausa/parada: os estados já foram revertidos parte a parte
        }
    }

    /// <summary>Progresso somado de N encodes em voo (fps = soma; speed = média ponderada pela duração).</summary>
    private void ProgressParallel(int jobId, double outTimeSum, double fpsSum, double speedAvg)
    {
        double done;
        lock (_counterGate)
            done = _jobDoneSeconds;
        var convertedVideo = _sessionConvertedSeconds + done + outTimeSum;
        var remainingVideo = Math.Max(0, _jobTotalSeconds - done - outTimeSum);
        var remaining = speedAvg > 0.05
            ? TimeSpan.FromSeconds(remainingVideo / speedAvg)
            : (TimeSpan?)null;
        LastStats = new QueueStats(
            fpsSum,
            speedAvg,
            convertedVideo / 60.0,
            _stopwatch.Elapsed,
            remaining,
            _currentLabel);
        StatsUpdated?.Invoke(LastStats);
        Progress?.Invoke(jobId, Math.Clamp((done + outTimeSum) / Math.Max(0.001, _jobTotalSeconds) * 100, 0, 100));
    }

    /// <summary>Somatório por slot dos encodes em paralelo; publica no QueueRunner a cada relato.</summary>
    private sealed class EncodeAggregator
    {
        private readonly object _gate = new();
        private readonly double[] _outTime;
        private readonly double[] _fps;
        private readonly double[] _speed;
        private readonly double[] _weight;
        private readonly Action<double, double, double> _report;

        public EncodeAggregator(int slots, Action<double, double, double> report)
        {
            _outTime = new double[slots];
            _fps = new double[slots];
            _speed = new double[slots];
            _weight = new double[slots];
            _report = report;
        }

        public IProgress<EncodeProgress> Slot(int slot) => new Progress<EncodeProgress>(p =>
        {
            lock (_gate)
            {
                _outTime[slot] = p.OutTimeSeconds;
                _fps[slot] = p.Fps;
                _speed[slot] = p.Speed;
            }
            Publish();
        });

        public void Begin(int slot, double duration)
        {
            lock (_gate)
            {
                _outTime[slot] = 0;
                _fps[slot] = 0;
                _speed[slot] = 0;
                _weight[slot] = duration;
            }
        }

        public void End(int slot)
        {
            lock (_gate)
            {
                _outTime[slot] = 0;
                _fps[slot] = 0;
                _speed[slot] = 0;
            }
            Publish();
        }

        private void Publish()
        {
            double outSum = 0, fpsSum = 0, speedNum = 0, speedDen = 0;
            lock (_gate)
            {
                for (var i = 0; i < _outTime.Length; i++)
                {
                    outSum += _outTime[i];
                    fpsSum += _fps[i];
                    if (_weight[i] > 0)
                    {
                        speedNum += _speed[i] * _weight[i];
                        speedDen += _weight[i];
                    }
                }
            }
            _report(outSum, fpsSum, speedDen > 0 ? speedNum / speedDen : 0);
        }
    }

    /// <summary>
    /// Modo "Apenas upscaling": o episódio INTEIRO é ampliado de uma vez, sem dividir por
    /// capítulos e sem encode AV1 — sai um MKV x264 CRF 14 com áudio, legendas, anexos e
    /// capítulos originais preservados.
    /// </summary>
    private async Task ProcessOnlyUpscaleAsync(Job job, UpscaleService upscale, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var outputRoot = AppServices.GetOutputDirectory();
        var workDir = Path.Combine(outputRoot, "AnimeBatch", SafeFolder(baseName));
        Directory.CreateDirectory(workDir);

        var info = await AppServices.Probe!.ProbeAsync(job.SourcePath, ct).ConfigureAwait(false);
        var duration = Math.Max(0.001, info.DurationSeconds);
        var finalPath = Path.Combine(outputRoot, $"{baseName} (upscaling).mkv");

        _jobTotalSeconds = duration;
        _jobDoneSeconds = 0;
        _currentLabel = $"{job.SeriesName ?? baseName} - upscaling → {job.UpscaleTargetHeight}p";
        EmitStats();

        // Retomada: arquivo final completo já presente não refaz o upscale
        if (!File.Exists(finalPath))
        {
            var progress = new Progress<EncodeProgress>(p => ProgressInternal(job.Id, p));
            try
            {
                var req = new UpscalePartRequest(
                    job.SourcePath, 0, duration,
                    Path.Combine(workDir, "ups_frames_full"), finalPath,
                    info.Height, info.Fps, job.UpscaleTargetHeight ?? 0,
                    job.UpscaleModel ?? "realcugan", UpscalerExe(job.UpscaleModel),
                    LosslessIntermediate: false, CopyAudio: true, KeepChapters: true, CopySubtitles: true,
                    GpuIds: await ResolveUpscaleGpusAsync(job.UpscaleModel, UpscalerExe(job.UpscaleModel)).ConfigureAwait(false),
                    SourceWidth: info.Width,
                    OnnxModelsDir: AppServices.Tools.OnnxModelsDir);
                await RunUpscaleStageAsync(upscale, req, ct, progress).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(finalPath); // arquivo parcial não serve de retomada
                throw;
            }
        }

        foreach (var item in job.Items)
            await AppServices.Jobs.SetItemStateAsync(item.Id, JobItemState.Done, finalPath).ConfigureAwait(false);

        _jobDoneSeconds += duration;
        _sessionConvertedSeconds += duration;

        var series = await AppServices.Series
            .FindByNormalizedNameAsync(ChapterService.CleanSeriesName(baseName))
            .ConfigureAwait(false);
        await AppServices.Conversions.AddAsync(new ConversionRecord
        {
            SeriesId = series?.Id,
            SeriesName = job.SeriesName ?? series?.Name ?? "",
            ConvertedAt = DateTime.Now,
            FileName = Path.GetFileName(finalPath),
            DurationSeconds = duration,
            SizeBytes = File.Exists(finalPath) ? new FileInfo(finalPath).Length : 0,
            OutputPath = finalPath,
        }).ConfigureAwait(false);

        await FinishJobAsync(job).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Conclusão compartilhada: estado Done, som e auto-remoção da fila.</summary>
    private async Task FinishJobAsync(Job job)
    {
        await AppServices.Jobs.SetStateAsync(job.Id, JobState.Done).ConfigureAwait(false);
        AppServices.PlayCompletionSound();

        // Checkbox "remover da fila ao ser convertido": job concluído sai da lista
        if (_autoRemove)
            await AppServices.Jobs.DeleteAsync(job.Id).ConfigureAwait(false);
    }

    /// <summary>Publica fps/velocidade/percentual de um estágio do job em execução.</summary>
    private void ProgressInternal(int jobId, EncodeProgress p)
    {
        var convertedVideo = _sessionConvertedSeconds + _jobDoneSeconds + p.OutTimeSeconds;
        var remainingVideo = Math.Max(0, _jobTotalSeconds - _jobDoneSeconds - p.OutTimeSeconds);
        var remaining = p.Speed > 0.05
            ? TimeSpan.FromSeconds(remainingVideo / p.Speed)
            : (TimeSpan?)null;
        LastStats = new QueueStats(
            p.Fps,
            p.Speed,
            convertedVideo / 60.0,
            _stopwatch.Elapsed,
            remaining,
            _currentLabel);
        StatsUpdated?.Invoke(LastStats);
        Progress?.Invoke(jobId, Math.Clamp((_jobDoneSeconds + p.OutTimeSeconds) / Math.Max(0.001, _jobTotalSeconds) * 100, 0, 100));
    }

    /// <summary>Executável ncnn-vulkan do modelo do job; vazio se a ferramenta não estiver instalada.</summary>
    private static string UpscalerExe(string? modelCode) =>
        modelCode == "realesrgan"
            ? AppServices.Tools.RealesrganPath ?? ""
            : AppServices.Tools.RealCuganPath ?? "";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // intermediário preso por outro processo não deve derrubar a fila
        }
    }

    private static string SafeFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "episodio" : s;
    }
}
