using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using System.Diagnostics;
using System.Globalization;

namespace AnimeBatch.Core.Queueing;

/// <summary>Estatísticas do que está rodando agora (rodapé da aplicação).
/// EncodePass > 0 = passada corrente de um encode multipass (SVT 2-pass): 1 = análise,
/// 2 = final; 0 = passada única. A UI traduz ("passo 1/2") — o Core só reporta o dado.
/// Rate = bitrate alvo ("500 kbps") ou qualidade constante ("CQ 22") do encode em curso.
/// ChunksDone/ChunksTotal/Phase = motor Av1an (contador de chunks e fase "scenes"/
/// "segmenting"/"chunks" — a UI traduz); no av1an não existe "passo 1/2", as passadas
/// rodam dentro de cada chunk. Workers = quando o encode é paralelo, uma estatística por
/// slot ativo (a UI mostra uma linha por worker); null = encode sequencial.</summary>
public record WorkerStat(string Title, double Fps, double Speed, string Rate,
    int ChunksDone = 0, int ChunksTotal = 0);

public record QueueStats(
    double Fps,
    double Speed,
    double ConvertedMinutes,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string CurrentLabel,
    int EncodePass = 0,
    string Rate = "",
    WorkerStat[]? Workers = null,
    int ChunksDone = 0,
    int ChunksTotal = 0,
    string Phase = "");

/// <summary>Estágio de encode, abstraído para o QueueRunner ser testável sem ffmpeg real
/// (EncodeService implementa; os testes da fila usam fakes).</summary>
public interface IEncodeStage
{
    Task EncodePartAsync(
        string sourcePath, EncodeService.JobItemRef item, string outputPath, string passLogBase,
        CodecEncodeConfig cfg, int kbps, CancellationToken ct,
        IProgress<EncodeProgress>? progress, int? cudaGpu = null);
}

/// <summary>Estágio de remux final (mkvmerge), abstraído pelos mesmos motivos.</summary>
public interface IMergeStage
{
    Task MergeAsync(
        IReadOnlyList<(string PartPath, string Title, double StartSeconds, double EndSeconds, bool IsCritical)> parts,
        string finalPath, string chaptersPath, CancellationToken ct);
}

/// <summary>Estágio de upscale nos dois motores (ncnn legado e ONNX/DirectML).</summary>
public interface IUpscaleStage
{
    Task UpscalePartAsync(UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress> progress);
    Task UpscalePartOnnxAsync(UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress>? progress);
}

/// <summary>Probe da origem (ffprobe), abstraído para a fila rodar sem processo real.</summary>
public interface IProbeStage
{
    Task<EpisodeInfo> ProbeAsync(string videoPath, CancellationToken ct = default);
}

/// <summary>Pedido de QC de qualidade: referência = trecho da origem, distorcida = parte encodeada.</summary>
public sealed record QualityCheckRequest(string RefPath, double RefStartSeconds, double RefDurationSeconds, string DistPath);

/// <summary>Resultado da QC (médias pooled do libvmaf).</summary>
public sealed record QualityResult(double Vmaf, double Ssim, double Psnr);

/// <summary>QC de qualidade opcional (libvmaf), abstraída pelos mesmos motivos dos demais estágios
/// (a fila roda em teste sem ffmpeg real; a implementação é QualityCheckService).</summary>
public interface IQualityCheckStage
{
    /// <summary>Null = não medida (probe falhou, ffmpeg falhou, timeout) — nunca lança para falha de medição.</summary>
    Task<QualityResult?> MeasureAsync(QualityCheckRequest req, CancellationToken ct);
}

/// <summary>
/// Dependências do QueueRunner. Tudo que toca banco, processo ou máquina entra aqui — o
/// App monta com as implementações reais (AppServices); os testes da fila montam com
/// SQLite em temp + fakes dos estágios. O runner não conhece UI nem estáticos.
/// </summary>
public sealed class QueueRunnerDeps
{
    public required JobRepository Jobs { get; init; }
    public required SettingsRepository Settings { get; init; }
    public required SeriesRepository Series { get; init; }
    public required ConversionRepository Conversions { get; init; }
    public required EncodeConfigRepository EncodeConfigs { get; init; }
    public required ToolsLocator Tools { get; init; }
    public required IProbeStage? Probe { get; init; }

    /// <summary>Fábrica do estágio de encode (recebe o timeout de stall lido da setting
    /// queue.stallMinutes a cada execução da fila).</summary>
    public required Func<TimeSpan, IEncodeStage> Encode { get; init; }
    public required Func<IMergeStage> Merge { get; init; }
    public required Func<IUpscaleStage> Upscale { get; init; }

    /// <summary>Fábrica da QC de qualidade opcional (VMAF). Null = QC indisponível (a fila
    /// segue normal, sem medir) — o App só monta quando ffmpeg/ffprobe existem.</summary>
    public Func<IQualityCheckStage>? Quality { get; init; }

    /// <summary>Raiz de saída (o App devolve a setting output.dir com fallback pra Vídeos).</summary>
    public required Func<string> OutputDirectory { get; init; }

    /// <summary>Log de exceções de processamento (o App grava em data\crash.log). Null = silencioso.</summary>
    public Action<string, Exception>? LogCrash { get; init; }

    /// <summary>Som de conclusão por episódio (o App toca; o Core não conhece áudio). Null = silêncio.</summary>
    public Action? CompletionSound { get; init; }

    /// <summary>Ação "quando terminar" da fila (setting queue.whenDone: none/shutdown/
    /// hibernate/sleep/logoff/lock/exit). O Core não executa nada — o App decide como
    /// desligar/fechar. Null = nada.</summary>
    public Action<string>? WhenDone { get; init; }
}

/// <summary>
/// Executa a fila: pega o menor Order pendente, encada cada parte com o codec do job,
/// junta com mkvmerge (capítulos cumulativos; Critical não vira capítulo), grava o
/// histórico e avisa a conclusão por episódio. Um job por vez, em sequência.
///
/// Pausar: interrompe, job volta como Paused; Iniciar retoma (partes Done são puladas).
/// Parar: interrompe, job volta como Pending (a parte em curso recomeça do zero).
/// </summary>
public class QueueRunner
{
    private readonly QueueRunnerDeps _d;

    private CancellationTokenSource? _cts;
    private bool _pauseRequested;
    private bool _autoRemove;
    private bool _qualityCheck;
    private readonly Stopwatch _stopwatch = new();
    private double _sessionConvertedSeconds;
    private double _jobDoneSeconds;
    private double _jobTotalSeconds;
    private string _currentLabel = "";
    private string _currentRate = "";

    public QueueRunner(QueueRunnerDeps deps)
    {
        _d = deps;
    }

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
        await _d.Jobs.ResetErrorJobsAsync().ConfigureAwait(false);
        await _d.Jobs.ResetPausedJobsAsync().ConfigureAwait(false);
        // Job Running órfão = app morto no meio do processamento (kill/crash) — volta pra fila
        await _d.Jobs.ResetRunningJobsAsync().ConfigureAwait(false);

        // "Remover da fila ao ser convertido": lido uma vez por execução
        _autoRemove = await _d.Settings
            .GetAsync(SettingsRepository.QueueAutoRemove)
            .ConfigureAwait(false) == "true";
        // QC de qualidade (VMAF) também é lida uma vez por execução; liga/desliga na fila.
        _qualityCheck = await _d.Settings
            .GetAsync(SettingsRepository.QueueQualityCheck)
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
            _currentLabel,
            Rate: _currentRate);
        StatsUpdated?.Invoke(LastStats);
    }

    /// <summary>Timeout de stall do encode (setting "queue.stallMinutes", em minutos;
    /// vazio/inválido = default de 10). Clamp 1–120: valor absurdo não pode desligar a
    /// proteção nem matar um encode saudável.</summary>
    private async Task<TimeSpan> ReadStallTimeoutAsync()
    {
        try
        {
            var raw = await _d.Settings.GetAsync(SettingsRepository.StallMinutes).ConfigureAwait(false);
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
        var tools = _d.Tools;
        if (tools.FfmpegPath is null || tools.MkvMergePath is null || _d.Probe is null)
            throw new InvalidOperationException("Ferramentas essenciais ausentes (ffmpeg/mkvmerge/ffprobe).");

        var encode = _d.Encode(await ReadStallTimeoutAsync().ConfigureAwait(false));
        var merge = _d.Merge();
        var upscale = _d.Upscale();

        while (!ct.IsCancellationRequested)
        {
            var job = await _d.Jobs.PeekNextPendingAsync().ConfigureAwait(false);
            if (job is null)
                break;

            await _d.Jobs.SetStateAsync(job.Id, JobState.Running).ConfigureAwait(false);
            _currentLabel = job.SeriesName ?? Path.GetFileNameWithoutExtension(job.SourcePath);
            _currentRate = "";
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
                await _d.Jobs.SetStateAsync(job.Id, finalState).ConfigureAwait(false);
                Changed?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                _d.LogCrash?.Invoke("QueueRunner", ex);
                await _d.Jobs.SetStateAsync(job.Id, JobState.Error, ShortError(ex)).ConfigureAwait(false);
                Changed?.Invoke();
                // segue pro próximo job da fila
            }
        }

        // A fila esvaziou de verdade (não foi Parar/Pausar — o cancelamento retorna antes):
        // dispara a ação "quando terminar" escolhida na tela da fila.
        var whenDone = await _d.Settings.GetAsync(SettingsRepository.QueueWhenDone).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(whenDone) && whenDone != "none")
            _d.WhenDone?.Invoke(whenDone);
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
            await _d.Settings.GetAsync(HardwareGpuService.SettingKey).ConfigureAwait(false));
        if (cards.Count > 0)
        {
            var resolved = modelCode == OnnxUpscaleService.MotorCode
                ? GpuSelector.ResolveOnnx(cards, _d.Tools.OnnxModelsDir)
                : await GpuSelector.ResolveNcnnAsync(cards, upscalerExe).ConfigureAwait(false);
            if (resolved is not null)
            {
                if (resolved.Count == 0)
                    throw NoGpuEnabled();
                return resolved;
            }
        }

        return ParseGpuList(await _d.Settings.GetAsync(SettingsRepository.UpscaleGpus).ConfigureAwait(false));
    }

    /// <summary>Escolhe o motor pelo código do job: "onnx" (AnimeJaNai in-process) ou os ncnn legados.</summary>
    private static Task RunUpscaleStageAsync(
        IUpscaleStage upscale, UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress> progress) =>
        req.ModelCode == OnnxUpscaleService.MotorCode
            ? upscale.UpscalePartOnnxAsync(req, ct, progress)
            : upscale.UpscalePartAsync(req, ct, progress);

    /// <summary>Pendência de encode montada depois do estágio de upscale (ou direto, sem upscale).
    /// Cfg = config do codec com os overrides DO CAPÍTULO aplicados (Preset/Cq do JobItem
    /// vencem a config da aba Encodes); Rate = "500 kbps" ou "CQ 22" para o rodapé.</summary>
    private sealed record PendingEncode(
        JobItem Item, string OutPath, string? IntermediatePath,
        string SourcePath, EncodeService.JobItemRef Ref, string PassLogBase, double Duration,
        CodecEncodeConfig Cfg, string Rate);

    /// <summary>Config efetiva da parte: overrides por capítulo (Preset/Cq) vencem a config
    /// do codec. Null nos dois = config original (caminho comum, sem capítulo customizado).</summary>
    private static CodecEncodeConfig EffectiveCfg(CodecEncodeConfig cfg, JobItem item) =>
        item.Preset is null && item.Cq is null
            ? cfg
            : cfg with { Preset = item.Preset ?? cfg.Preset, Cq = item.Cq ?? cfg.Cq };

    /// <summary>Rótulo do rodapé para o encode em curso: CQ (Qualidade Constante, com o
    /// override do capítulo quando houver) ou o bitrate alvo da parte.</summary>
    private static string RateFor(CodecEncodeConfig effectiveCfg, JobItem item) =>
        effectiveCfg.UseConstantQuality
            ? $"CQ {effectiveCfg.Cq}"
            : $"{item.TargetKbps} kbps";

    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly object _counterGate = new();

    /// <summary>Escrita de estado de item serializada — o encode paralelo dispara N trabalhadores
    /// e o SQLite não gosta de escritas concorrentes.</summary>
    private async Task SetItemGatedAsync(int itemId, JobItemState state, string? outputPath = null)
    {
        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _d.Jobs.SetItemStateAsync(itemId, state, outputPath).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>Grava o resultado da QC serializado pelo mesmo _dbGate (escritas SQLite).</summary>
    private async Task SetQualityGatedAsync(int itemId, QualityResult q)
    {
        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _d.Jobs.SetItemQualityAsync(itemId, q.Vmaf, q.Ssim, q.Psnr).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>QC de qualidade da parte (setting queue.qualityCheck): libvmaf origem×parte
    /// gravado no item. BEST-EFFORT: qualquer falha é só logada — medição nunca derruba a fila
    /// nem muda o estado (Done) que a parte já tem. Roda ANTES do delete do intermediário
    /// (com upscale a referência é ele, para isolar a qualidade do encode).</summary>
    private async Task RunQualityCheckAsync(PendingEncode e, CancellationToken ct)
    {
        if (!_qualityCheck || _d.Quality is null)
            return;
        try
        {
            var req = new QualityCheckRequest(e.SourcePath, e.Ref.StartSeconds, e.Duration, e.OutPath);
            var result = await _d.Quality().MeasureAsync(req, ct).ConfigureAwait(false);
            if (result is { } q)
                await SetQualityGatedAsync(e.Item.Id, q).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // pausa/parada no meio da QC: a parte já está Done; a medição fica sem resultado
        }
        catch (Exception ex)
        {
            _d.LogCrash?.Invoke("QualityCheck", ex);
        }
    }

    private async Task ProcessJobAsync(Job job, IEncodeStage encode, IMergeStage merge, IUpscaleStage upscale, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var outputRoot = _d.OutputDirectory();
        var workDir = JobPaths.WorkDirectory(outputRoot, baseName);
        Directory.CreateDirectory(workDir);

        // Dimensões/fps da origem guiam o plano de upscale (fator do modelo, escala final)
        var info = await _d.Probe!.ProbeAsync(job.SourcePath, ct).ConfigureAwait(false);
        var upscaledIntermediates = job.UpscaleMode == UpscaleMode.WithEncode;

        var items = job.Items.OrderBy(i => i.Order).ToList();
        _jobTotalSeconds = items.Sum(i => Math.Max(0.001, i.EndSeconds - i.StartSeconds));
        _jobDoneSeconds = 0;
        var codec = job.VideoCodec ?? "nvenc_av1_10bit";
        var cfg = await _d.EncodeConfigs.GetAsync(codec).ConfigureAwait(false);
        var isNvenc = codec.StartsWith("nvenc", StringComparison.Ordinal);
        var doneParts = new List<(JobItem Item, string Path)>();

        // Fase 1: upscale (uma parte por vez — o estágio já paraleliza dentro) e montagem
        // das pendências de encode. Fase 2: encode sequencial ou em paralelo por GPU.
        var pending = new List<PendingEncode>();
        for (var pos = 0; pos < items.Count; pos++)
        {
            var item = items[pos];
            ct.ThrowIfCancellationRequested();

            var outPath = Path.Combine(workDir,
                JobPaths.PartFileName(item.Order, item.Title, baseName));
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
                await _d.Jobs.SetItemStateAsync(item.Id, JobItemState.Upcaling).ConfigureAwait(false);
                // rótulo usa POSIÇÃO na lista — o Order pode ter buracos (capítulos marcados
                // seletivamente viram partes 02, 05… sem os demais)
                _currentLabel = $"{job.SeriesName ?? baseName} - {pos + 1}/{items.Count} - {item.Title} · upscaling";
                _currentRate = "";
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
                        OnnxModelsDir: _d.Tools.OnnxModelsDir,
                        FpsRatio: info.FpsRatio);
                    await RunUpscaleStageAsync(upscale, req, ct, upsProgress).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await _d.Jobs.SetItemStateAsync(item.Id, JobItemState.Pending).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _d.LogCrash?.Invoke("UpscaleService", ex);
                    await _d.Jobs.SetItemStateAsync(item.Id, JobItemState.Error).ConfigureAwait(false);
                    throw; // marca o job como Error no chamador e segue a fila
                }
            }

            // Com upscale, o intermediário contém SÓ a parte → corte relativo começa em 0
            var itemCfg = EffectiveCfg(cfg, item);
            pending.Add(new PendingEncode(
                item, outPath, upscaledIntermediates ? intermediatePath : null,
                upscaledIntermediates ? intermediatePath : job.SourcePath,
                upscaledIntermediates
                    ? new EncodeService.JobItemRef(0, itemDuration, item.TargetKbps)
                    : new EncodeService.JobItemRef(item.StartSeconds, item.EndSeconds, item.TargetKbps),
                Path.Combine(workDir, $"pass_{item.Order:00}"),
                itemDuration,
                itemCfg,
                RateFor(itemCfg, item)));
        }

        // ---- Fase 2: encode das partes pendentes ----
        if (pending.Count > 0)
        {
            // Pool de encode NVENC: workers por placa da tela Hardware (GPU) no espaço
            // NVENC/nvidia-smi; todas as placas em 0 = erro claro (decisão final do usuário).
            // Fora do NVENC: encodes paralelos no CPU se o codec pedir (ParallelWorkers 2–3,
            // seletor da aba Encodes) — cada worker roda o comando SVT normal, SEM -gpu;
            // 1 worker (default) segue sequencial como sempre.
            IReadOnlyList<int?>? pool = null;
            if (isNvenc)
            {
                var cardsJson = await _d.Settings.GetAsync(HardwareGpuService.SettingKey).ConfigureAwait(false);
                var gpuPool = NvencGpuProbe.ResolveWorkers(cardsJson);
                if (gpuPool is { Count: 0 })
                    throw NoGpuEnabled();
                pool = gpuPool?.Cast<int?>().ToList();
            }
            else if (cfg.EffectiveParallelWorkers > 1)
            {
                pool = Enumerable.Repeat<int?>(null, cfg.EffectiveParallelWorkers).ToList();
            }

            if (pool is { Count: > 0 })
                await EncodePendingParallelAsync(job, encode, cfg, pending, pool, ct).ConfigureAwait(false);
            else
                await EncodePendingSequentialAsync(job, encode, cfg, pending, ct).ConfigureAwait(false);

            foreach (var e in pending)
                doneParts.Add((e.Item, e.OutPath));
        }

        // Junção: capítulos cumulativos, uma entrada por parte não-crítica (no encode
        // paralelo a conclusão pode sair fora de ordem → ordena pelo Order). Capítulos
        // TEMPORÁRIOS seguem o mesmo tratamento de Critical: o conteúdo entra no vídeo,
        // mas não ganham entrada de capítulo no final ("como se não existissem") —
        // servem só para dividir o encode com bitrate próprio.
        var finalPath = Path.Combine(outputRoot, $"{baseName}.mkv");
        var chaptersPath = Path.Combine(workDir, $"{baseName}_chapters.txt");
        var mergeParts = doneParts
            .OrderBy(p => p.Item.Order)
            .Select(e => (PartPath: e.Path, e.Item.Title, e.Item.StartSeconds, e.Item.EndSeconds,
                IsCritical: e.Item.Class == BitrateClass.Critical || e.Item.IsTemporary))
            .ToList();
        await merge.MergeAsync(mergeParts, finalPath, chaptersPath, ct).ConfigureAwait(false);

        // Histórico (tela de Séries → Arquivos Convertidos)
        var series = await _d.Series
            .FindByNormalizedNameAsync(ChapterService.CleanSeriesName(baseName))
            .ConfigureAwait(false);
        await _d.Conversions.AddAsync(new ConversionRecord
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
        Job job, IEncodeStage encode, CodecEncodeConfig cfg,
        IReadOnlyList<PendingEncode> pending, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var total = pending.Count;

        for (var pos = 0; pos < pending.Count; pos++)
        {
            var e = pending[pos];
            ct.ThrowIfCancellationRequested();

            await SetItemGatedAsync(e.Item.Id, JobItemState.Encoding).ConfigureAwait(false);
            // posição na lista, não Order (que pode pular: só os capítulos marcados entram)
            _currentLabel = $"{job.SeriesName ?? baseName} - {pos + 1}/{total} - {e.Item.Title}";
            _currentRate = e.Rate;
            EmitStats();

            var progress = new Progress<EncodeProgress>(p => ProgressInternal(job.Id, p));
            try
            {
                await encode.EncodePartAsync(
                    e.SourcePath, e.Ref, e.OutPath, e.PassLogBase, e.Cfg, e.Item.TargetKbps, ct, progress).ConfigureAwait(false);

                await SetItemGatedAsync(e.Item.Id, JobItemState.Done, e.OutPath).ConfigureAwait(false);
                await RunQualityCheckAsync(e, ct).ConfigureAwait(false);
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
                _d.LogCrash?.Invoke("EncodeService", ex);
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
    /// Encode paralelo das partes: cada worker pega a próxima pendência e roda um encode.
    /// Slots do pool: índice de placa NVENC (-gpu N, workers por placa = tela Hardware (GPU))
    /// ou null = CPU (SVT em paralelo, comando normal sem -gpu). Uma parte que falha para de
    /// pegar novas mas deixa as em voo terminarem.
    /// </summary>
    private async Task EncodePendingParallelAsync(
        Job job, IEncodeStage encode, CodecEncodeConfig cfg,
        IReadOnlyList<PendingEncode> pending, IReadOnlyList<int?> pool, CancellationToken ct)
    {
        var workerCount = Math.Min(pool.Count, pending.Count);
        var name = job.SeriesName ?? Path.GetFileNameWithoutExtension(job.SourcePath);
        _currentLabel = $"{name} - encode paralelo ({pending.Count} partes · {workerCount} workers)";
        _currentRate = ""; // o rate agora vive por worker (linhas individuais do rodapé)
        EmitStats();

        var agg = new EncodeAggregator(workerCount, (outSum, fps, speed, workers) => ProgressParallel(job.Id, outSum, fps, speed, workers));
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
                    agg.Begin(slot, e.Duration, e.Item.Title, e.Rate);
                    try
                    {
                        await SetItemGatedAsync(e.Item.Id, JobItemState.Encoding).ConfigureAwait(false);
                        await encode.EncodePartAsync(
                            e.SourcePath, e.Ref, e.OutPath, e.PassLogBase, e.Cfg, e.Item.TargetKbps, ct, progress, gpu).ConfigureAwait(false);

                        await SetItemGatedAsync(e.Item.Id, JobItemState.Done, e.OutPath).ConfigureAwait(false);
                        await RunQualityCheckAsync(e, ct).ConfigureAwait(false);
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
                        _d.LogCrash?.Invoke("EncodeService", ex);
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

    /// <summary>Progresso somado de N encodes em voo (fps = soma; speed = média ponderada pela duração)
    /// + uma estatística por worker ativo para o rodapé multi-linha.</summary>
    private void ProgressParallel(int jobId, double outTimeSum, double fpsSum, double speedAvg, WorkerStat[] workers)
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
            _currentLabel,
            Workers: workers);
        StatsUpdated?.Invoke(LastStats);
        Progress?.Invoke(jobId, Math.Clamp((done + outTimeSum) / Math.Max(0.001, _jobTotalSeconds) * 100, 0, 100));
    }

    /// <summary>Somatório por slot dos encodes em paralelo; publica no QueueRunner a cada relato.
    /// Guarda também título/rate/chunks por slot — slots ocupados viram linhas individuais no rodapé.</summary>
    private sealed class EncodeAggregator
    {
        private readonly object _gate = new();
        private readonly double[] _outTime;
        private readonly double[] _fps;
        private readonly double[] _speed;
        private readonly double[] _weight;
        private readonly string?[] _titles;
        private readonly string?[] _rates;
        private readonly int[] _chunksDone;
        private readonly int[] _chunksTotal;
        private readonly Action<double, double, double, WorkerStat[]> _report;

        public EncodeAggregator(int slots, Action<double, double, double, WorkerStat[]> report)
        {
            _outTime = new double[slots];
            _fps = new double[slots];
            _speed = new double[slots];
            _weight = new double[slots];
            _titles = new string?[slots];
            _rates = new string?[slots];
            _chunksDone = new int[slots];
            _chunksTotal = new int[slots];
            _report = report;
        }

        public IProgress<EncodeProgress> Slot(int slot) => new Progress<EncodeProgress>(p =>
        {
            lock (_gate)
            {
                _outTime[slot] = p.OutTimeSeconds;
                _fps[slot] = p.Fps;
                _speed[slot] = p.Speed;
                _chunksDone[slot] = p.ChunksDone;
                _chunksTotal[slot] = p.ChunksTotal;
            }
            Publish();
        });

        public void Begin(int slot, double duration, string title, string rate)
        {
            lock (_gate)
            {
                _outTime[slot] = 0;
                _fps[slot] = 0;
                _speed[slot] = 0;
                _weight[slot] = duration;
                _titles[slot] = title;
                _rates[slot] = rate;
                _chunksDone[slot] = 0;
                _chunksTotal[slot] = 0;
            }
            Publish();
        }

        public void End(int slot)
        {
            lock (_gate)
            {
                _outTime[slot] = 0;
                _fps[slot] = 0;
                _speed[slot] = 0;
                _titles[slot] = null; // slot livre sai das linhas de worker do rodapé
                _rates[slot] = null;
                _chunksDone[slot] = 0;
                _chunksTotal[slot] = 0;
            }
            Publish();
        }

        private void Publish()
        {
            double outSum = 0, fpsSum = 0, speedNum = 0, speedDen = 0;
            var workers = new List<WorkerStat>();
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
                    if (_titles[i] is { } title)
                        workers.Add(new WorkerStat(title, _fps[i], _speed[i], _rates[i] ?? "",
                            _chunksDone[i], _chunksTotal[i]));
                }
            }
            _report(outSum, fpsSum, speedDen > 0 ? speedNum / speedDen : 0, [.. workers]);
        }
    }

    /// <summary>
    /// Modo "Apenas upscaling": o episódio INTEIRO é ampliado de uma vez, sem dividir por
    /// capítulos e sem encode AV1 — sai um MKV x264 CRF 14 com áudio, legendas, anexos e
    /// capítulos originais preservados.
    /// </summary>
    private async Task ProcessOnlyUpscaleAsync(Job job, IUpscaleStage upscale, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.SourcePath);
        var outputRoot = _d.OutputDirectory();
        var workDir = JobPaths.WorkDirectory(outputRoot, baseName);
        Directory.CreateDirectory(workDir);

        var info = await _d.Probe!.ProbeAsync(job.SourcePath, ct).ConfigureAwait(false);
        var duration = Math.Max(0.001, info.DurationSeconds);
        var finalPath = Path.Combine(outputRoot, $"{baseName} (upscaling).mkv");

        _jobTotalSeconds = duration;
        _jobDoneSeconds = 0;
        _currentLabel = $"{job.SeriesName ?? baseName} - upscaling → {job.UpscaleTargetHeight}p";
        _currentRate = "";
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
                    OnnxModelsDir: _d.Tools.OnnxModelsDir,
                    FpsRatio: info.FpsRatio);
                await RunUpscaleStageAsync(upscale, req, ct, progress).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(finalPath); // arquivo parcial não serve de retomada
                throw;
            }
        }

        foreach (var item in job.Items)
            await _d.Jobs.SetItemStateAsync(item.Id, JobItemState.Done, finalPath).ConfigureAwait(false);

        _jobDoneSeconds += duration;
        _sessionConvertedSeconds += duration;

        var series = await _d.Series
            .FindByNormalizedNameAsync(ChapterService.CleanSeriesName(baseName))
            .ConfigureAwait(false);
        await _d.Conversions.AddAsync(new ConversionRecord
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
        await _d.Jobs.SetStateAsync(job.Id, JobState.Done).ConfigureAwait(false);
        _d.CompletionSound?.Invoke();

        // Checkbox "remover da fila ao ser convertido": job concluído sai da lista
        if (_autoRemove)
            await _d.Jobs.DeleteAsync(job.Id).ConfigureAwait(false);
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
            _currentLabel,
            p.Pass,
            _currentRate,
            ChunksDone: p.ChunksDone,
            ChunksTotal: p.ChunksTotal,
            Phase: p.Phase);
        StatsUpdated?.Invoke(LastStats);
        Progress?.Invoke(jobId, Math.Clamp((_jobDoneSeconds + p.OutTimeSeconds) / Math.Max(0.001, _jobTotalSeconds) * 100, 0, 100));
    }

    /// <summary>Executável ncnn-vulkan do modelo do job; vazio se a ferramenta não estiver instalada.</summary>
    private string UpscalerExe(string? modelCode) =>
        modelCode == "realesrgan"
            ? _d.Tools.RealesrganPath ?? ""
            : _d.Tools.RealCuganPath ?? "";

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

    // SafeFolder virou JobPaths.SafeFolder (Core.Queueing) — compartilhado com a tela de
    // Episódios, que precisa montar os mesmos caminhos para reaproveitar partes existentes.
}
