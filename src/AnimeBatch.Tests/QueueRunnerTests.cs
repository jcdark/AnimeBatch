using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Queueing;
using AnimeBatch.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Testes da FILA (orquestração): reset de órfãos, ordem de processamento, retomada de
/// partes, erro que não derruba a fila, pausa/parada e auto-remoção. SQLite REAL em temp
/// (mesma técnica do JobRepositoryTests) + FAKES dos estágios (encode/merge/upscale/probe)
/// — nenhum processo externo roda aqui; os processos reais têm suíte de integração própria.
/// </summary>
public class QueueRunnerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"animebatch-queue-{Guid.NewGuid():N}.db");
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), $"animebatch-queue-{Guid.NewGuid():N}");
    private readonly string _toolsDir;
    private readonly JobRepository _jobs;
    private readonly SettingsRepository _settings;

    public QueueRunnerTests()
    {
        Directory.CreateDirectory(_outDir);
        // "Binários" falsos: o runner valida ffmpeg/mkvmerge presentes antes de processar
        // (os fakes dos estágios nunca abrem processo — os arquivos só satisfazem a checagem)
        _toolsDir = Path.Combine(_outDir, "tools");
        Directory.CreateDirectory(_toolsDir);
        File.WriteAllText(Path.Combine(_toolsDir, "ffmpeg.exe"), "");
        File.WriteAllText(Path.Combine(_toolsDir, "mkvmerge.exe"), "");

        using var db = new AnimeBatchDbContext(_dbPath);
        db.Database.EnsureCreated();
        _jobs = new JobRepository(() => new AnimeBatchDbContext(_dbPath));
        _settings = new SettingsRepository(() => new AnimeBatchDbContext(_dbPath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_outDir)) Directory.Delete(_outDir, recursive: true); } catch { }
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static Job NewJob(string fileName, string codec = "svt_av1")
    {
        var job = new Job
        {
            SourcePath = Path.Combine(@"G:\Dublados", fileName),
            SeriesName = "Serie Teste",
            EpisodeKbps = 500,
            OpeningKbps = 1500,
            EndingKbps = 900,
            UpscaleMode = UpscaleMode.None,
            VideoCodec = codec,
        };
        job.Items.Add(new JobItem
        {
            Order = 1, Title = "Parte 1", StartSeconds = 0, EndSeconds = 60,
            Class = BitrateClass.Episode, TargetKbps = 500,
        });
        return job;
    }

    private QueueRunner NewRunner(
        FakeEncode encode, FakeMerge? merge = null)
    {
        return new QueueRunner(new QueueRunnerDeps
        {
            Jobs = _jobs,
            Settings = _settings,
            Series = new SeriesRepository(() => new AnimeBatchDbContext(_dbPath)),
            Conversions = new ConversionRepository(() => new AnimeBatchDbContext(_dbPath)),
            EncodeConfigs = new EncodeConfigRepository(_settings),
            Tools = new ToolsLocator(_toolsDir),
            Probe = new FakeProbe(),
            Encode = _ => encode,
            Merge = () => merge ?? new FakeMerge(),
            Upscale = () => new FakeUpscale(),
            OutputDirectory = () => _outDir,
        });
    }

    [Fact]
    public async Task Run_processa_na_ordem_e_restaura_orfaos_error_paused_running()
    {
        var a = NewJob("ep_a.mkv");
        var b = NewJob("ep_b.mkv");
        var c = NewJob("ep_c.mkv");
        var d = NewJob("ep_d.mkv");
        await _jobs.AddAsync(a); // Order 1 — processado primeiro
        await _jobs.AddAsync(b); // Order 2
        await _jobs.AddAsync(c); // Order 3
        await _jobs.AddAsync(d); // Order 4

        // Órfãos de sessões anteriores: Error / Paused / Running — RunAsync volta todos a processar
        await _jobs.SetStateAsync(b.Id, JobState.Error, "falha antiga");
        await _jobs.SetStateAsync(c.Id, JobState.Paused);
        await _jobs.SetStateAsync(d.Id, JobState.Running); // crash no meio do processamento

        var encode = new FakeEncode();
        var runner = NewRunner(encode);
        await runner.RunAsync();

        var all = await _jobs.GetAllOrderedAsync();
        Assert.All(all, j => Assert.Equal(JobState.Done, j.State));
        // A fila respeita o Order mesmo com estados espalhados antes do Run
        Assert.Equal(
            [Path.Combine(@"G:\Dublados", "ep_a.mkv"), Path.Combine(@"G:\Dublados", "ep_b.mkv"),
             Path.Combine(@"G:\Dublados", "ep_c.mkv"), Path.Combine(@"G:\Dublados", "ep_d.mkv")],
            encode.EncodedSources);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task Parte_done_com_arquivo_presente_pula_o_encode()
    {
        var job = NewJob("ep_r.mkv");
        await _jobs.AddAsync(job);

        // A parte foi concluída numa sessão anterior e o arquivo continua lá
        var baseName = "ep_r";
        var outPath = Path.Combine(_outDir, "AnimeBatch", baseName, "01 - Parte 1 - ep_r.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, "parte da sessão anterior");
        await _jobs.SetItemStateAsync(job.Items[0].Id, JobItemState.Done, outPath);

        var encode = new FakeEncode();
        var merge = new FakeMerge();
        var runner = NewRunner(encode, merge);
        await runner.RunAsync();

        Assert.Empty(encode.EncodedSources);      // nada re-encodado
        Assert.Equal(1, merge.MergeCalls);        // mas o merge acontece com a parte pronta
        var done = (await _jobs.GetAllOrderedAsync()).Single();
        Assert.Equal(JobState.Done, done.State);
        Assert.Equal(JobItemState.Done, done.Items[0].State);
    }

    [Fact]
    public async Task Erro_no_encode_marca_job_error_e_a_fila_segue()
    {
        var ruim = NewJob("ep_ruim.mkv");
        var bom = NewJob("ep_bom.mkv");
        await _jobs.AddAsync(ruim);
        await _jobs.AddAsync(bom);

        var encode = new FakeEncode();
        encode.OnEncode = source =>
        {
            if (source.EndsWith("ep_ruim.mkv", StringComparison.Ordinal))
                throw new InvalidOperationException("boom no encode");
        };
        var runner = NewRunner(encode);
        await runner.RunAsync();

        var all = await _jobs.GetAllOrderedAsync();
        var jobRuim = all.Single(j => j.SourcePath!.EndsWith("ep_ruim.mkv"));
        var jobBom = all.Single(j => j.SourcePath!.EndsWith("ep_bom.mkv"));
        Assert.Equal(JobState.Error, jobRuim.State);
        Assert.Contains("boom no encode", jobRuim.ErrorMessage);
        Assert.Equal(JobItemState.Error, jobRuim.Items[0].State);
        Assert.Equal(JobState.Done, jobBom.State); // a fila NÃO parou no erro
    }

    [Fact]
    public async Task Pausa_deixa_job_paused_e_item_pending()
    {
        var job = NewJob("ep_p.mkv");
        await _jobs.AddAsync(job);

        var encode = new FakeEncode();
        var runner = NewRunner(encode);
        encode.OnEncode = _ =>
        {
            runner.Pause();
            throw new OperationCanceledException();
        };
        await runner.RunAsync();

        var paused = (await _jobs.GetAllOrderedAsync()).Single();
        Assert.Equal(JobState.Paused, paused.State);
        Assert.Equal(JobItemState.Pending, paused.Items[0].State); // parte recomeça do zero
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task Parar_deixa_job_pending_e_nao_processa_os_proximos()
    {
        var job = NewJob("ep_s1.mkv");
        var job2 = NewJob("ep_s2.mkv");
        await _jobs.AddAsync(job);
        await _jobs.AddAsync(job2);

        var encode = new FakeEncode();
        var runner = NewRunner(encode);
        encode.OnEncode = _ =>
        {
            runner.Stop(); // sem _pauseRequested → é "parar", não "pausar"
            throw new OperationCanceledException();
        };
        await runner.RunAsync();

        var all = await _jobs.GetAllOrderedAsync();
        Assert.Equal(JobState.Pending, all.Single(j => j.SourcePath!.EndsWith("ep_s1.mkv")).State);
        Assert.Equal(JobState.Pending, all.Single(j => j.SourcePath!.EndsWith("ep_s2.mkv")).State);
    }

    [Fact]
    public async Task AutoRemove_apaga_job_concluido_da_fila()
    {
        await _settings.SetAsync(SettingsRepository.QueueAutoRemove, "true");
        var job = NewJob("ep_auto.mkv");
        await _jobs.AddAsync(job);

        var runner = NewRunner(new FakeEncode());
        await runner.RunAsync();

        Assert.Empty(await _jobs.GetAllOrderedAsync()); // concluído e removido
    }

    // ---- Fakes ----

    private sealed class FakeProbe : IProbeStage
    {
        public Task<EpisodeInfo> ProbeAsync(string videoPath, CancellationToken ct = default) =>
            Task.FromResult(new EpisodeInfo(videoPath, 60.0,
                Array.Empty<AudioStreamInfo>(), Array.Empty<SubtitleStreamInfo>(),
                Array.Empty<ChapterInfo>(), 1280, 720, 23.976));
    }

    private sealed class FakeEncode : IEncodeStage
    {
        /// <summary>Gancho por chamada (lançar para simular falha/pausa).</summary>
        public Action<string>? OnEncode;
        public List<string> EncodedSources = [];

        public Task EncodePartAsync(
            string sourcePath, EncodeService.JobItemRef item, string outputPath, string passLogBase,
            CodecEncodeConfig cfg, int kbps, CancellationToken ct,
            IProgress<EncodeProgress>? progress, int? cudaGpu = null)
        {
            OnEncode?.Invoke(sourcePath);
            File.WriteAllText(outputPath, "parte fake");
            EncodedSources.Add(sourcePath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMerge : IMergeStage
    {
        public int MergeCalls;

        public Task MergeAsync(
            IReadOnlyList<(string PartPath, string Title, double StartSeconds, double EndSeconds, bool IsCritical)> parts,
            string finalPath, string chaptersPath, CancellationToken ct)
        {
            MergeCalls++;
            File.WriteAllText(finalPath, "merge fake");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpscale : IUpscaleStage
    {
        public Task UpscalePartAsync(UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress> progress)
        {
            File.WriteAllText(req.OutputPath, "upscale fake");
            return Task.CompletedTask;
        }

        public Task UpscalePartOnnxAsync(UpscalePartRequest req, CancellationToken ct, IProgress<EncodeProgress>? progress)
        {
            File.WriteAllText(req.OutputPath, "upscale fake");
            return Task.CompletedTask;
        }
    }
}
