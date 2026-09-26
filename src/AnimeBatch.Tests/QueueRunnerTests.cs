using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Queueing;
using AnimeBatch.Core.Services;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
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
        FakeEncode encode, FakeMerge? merge = null, Func<IQualityCheckStage>? quality = null)
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
            Quality = quality,
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
    public async Task Merge_da_retomada_junta_a_parte_pronta_e_a_nova_na_ordem()
    {
        // cenário do usuário (18/09): converter 1 capítulo isolado não pode gerar um FINAL
        // só com aquele pedaço — a parte pronta de uma sessão anterior entra na junção
        var job = NewJob("ep_m.mkv");
        job.Items.Add(new JobItem
        {
            Order = 2, Title = "Parte 2", StartSeconds = 60, EndSeconds = 120,
            Class = BitrateClass.Episode, TargetKbps = 500,
        });
        await _jobs.AddAsync(job);

        // a parte 1 (capítulo anterior) já foi convertida e continua na pasta
        var donePath = Path.Combine(_outDir, "AnimeBatch", "ep_m", "01 - Parte 1 - ep_m.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(donePath)!);
        await File.WriteAllTextAsync(donePath, "parte 1 pronta");
        await _jobs.SetItemStateAsync(job.Items[0].Id, JobItemState.Done, donePath);

        var merge = new FakeMerge();
        var runner = NewRunner(new FakeEncode(), merge);
        await runner.RunAsync();

        Assert.Equal(1, merge.MergeCalls);
        Assert.Equal(2, merge.LastParts.Count); // as DUAS partes foram juntadas
        Assert.EndsWith("01 - Parte 1 - ep_m.mkv", merge.LastParts[0].PartPath);
        Assert.EndsWith("02 - Parte 2 - ep_m.mkv", merge.LastParts[1].PartPath);
    }

    [Fact]
    public async Task Capitulo_temporario_entra_no_video_mas_nao_vira_capitulo()
    {
        // "Capítulo temporário": encodeia como parte (conteúdo entra no final) mas NÃO
        // recebe entrada de capítulo — o merge o recebe com a mesma flag de Critical
        var job = NewJob("ep_tmp.mkv");
        job.Items.Add(new JobItem
        {
            Order = 2, Title = "Temporaria", StartSeconds = 60, EndSeconds = 120,
            Class = BitrateClass.Episode, TargetKbps = 500, IsTemporary = true,
        });
        await _jobs.AddAsync(job);

        var merge = new FakeMerge();
        var runner = NewRunner(new FakeEncode(), merge);
        await runner.RunAsync();

        Assert.Equal(2, merge.LastParts.Count); // parte 1 normal + parte 2 temporária
        Assert.False(merge.LastParts[0].IsCritical);   // parte normal vira capítulo como sempre
        Assert.True(merge.LastParts[1].IsCritical, "capítulo temporário não pode virar entrada de capítulo no final");

        // as partes foram encodeadas normalmente (o temporário existe SÓ para dividir o encode)
        var done = (await _jobs.GetAllOrderedAsync()).Single();
        Assert.Equal(JobState.Done, done.State);
        Assert.All(done.Items, i => Assert.Equal(JobItemState.Done, i.State));
    }

    [Fact]
    public async Task Capitulo_normal_continua_virando_capitulo_no_final()
    {
        // guarda do comportamento: sem temporário/critical, a parte vira capítulo como sempre
        var job = NewJob("ep_norm.mkv");
        await _jobs.AddAsync(job);

        var merge = new FakeMerge();
        var runner = NewRunner(new FakeEncode(), merge);
        await runner.RunAsync();

        var (_, isCritical) = Assert.Single(merge.LastParts);
        Assert.False(isCritical);
    }

    [Fact]
    public void JobPaths_bate_com_a_convencao_das_partes()
    {
        // nome da parte = "NN - título sanitizado - base.mkv"; pasta = raiz\AnimeBatch\base
        Assert.Equal(
            Path.Combine("raiz", "AnimeBatch", "KAIJU GIRL CARAMELISE - S01E08", "02 - Intro - KAIJU GIRL CARAMELISE - S01E08.mkv"),
            JobPaths.PartPath("raiz", "KAIJU GIRL CARAMELISE - S01E08", 2, "Intro"));
        Assert.Equal("episodio", JobPaths.SafeFolder("///"));
        Assert.Equal("base", JobPaths.SafeFolder("  base  "));
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

    [Fact]
    public async Task Passada_do_encode_multipass_chega_ao_rodape_via_StatsUpdated()
    {
        var job = NewJob("ep_pass.mkv", codec: "svt_av1_10bit");
        await _jobs.AddAsync(job);

        var encode = new FakeEncode { PassReports = [1, 2] };
        var runner = NewRunner(encode);
        var visto = new ConcurrentDictionary<int, bool>();
        var passo2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.StatsUpdated += s =>
        {
            if (s.EncodePass > 0)
            {
                visto[s.EncodePass] = true;
                if (s.EncodePass == 2)
                    passo2.TrySetResult();
            }
        };

        await runner.RunAsync();

        // Progress<T> posta assíncrono — espera o relato do passo 2 (o 1 vem antes) com teto
        Assert.True(await Task.WhenAny(passo2.Task, Task.Delay(10_000)) == passo2.Task,
            "o relato da passada 2 não chegou ao StatsUpdated");
        Assert.True(visto[1], "a passada 1 não foi relatada");
    }

    [Fact]
    public async Task Encode_svt_paralelo_2_workers_rodam_sobrepostos_e_sem_gpu()
    {
        // ParallelWorkers=2 é o que a aba Encodes salva para o codec svt_av1
        await _settings.SetAsync("encode.cfg.svt_av1",
            System.Text.Json.JsonSerializer.Serialize(new CodecEncodeConfig { Code = "svt_av1", ParallelWorkers = 2 }));

        var job = NewJob("ep_par.mkv");
        job.Items.Add(new JobItem
        {
            Order = 2, Title = "Parte 2", StartSeconds = 60, EndSeconds = 120,
            Class = BitrateClass.Episode, TargetKbps = 500,
        });
        await _jobs.AddAsync(job);

        var encode = new FakeEncode();
        var inFlight = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        encode.OnEncode = _ =>
        {
            // o primeiro encode que entrar ESPERA o segundo começar (teto 3s): se a fila
            // estivesse sequencial, ninguém liberaria e o teste falha sem flake de timing
            if (Interlocked.Increment(ref inFlight) == 2)
                bothStarted.TrySetResult();
            bothStarted.Task.Wait(3_000);
            Interlocked.Decrement(ref inFlight);
        };

        var runner = NewRunner(encode);
        await runner.RunAsync();

        Assert.True(bothStarted.Task.IsCompleted, "os 2 workers não rodaram ao mesmo tempo");
        Assert.Equal(2, encode.EncodedSources.Count);
        Assert.All(encode.SeenGpus, g => Assert.Null(g)); // CPU: nenhum -gpu no comando
        var done = (await _jobs.GetAllOrderedAsync()).Single();
        Assert.Equal(JobState.Done, done.State);
        Assert.All(done.Items, i => Assert.Equal(JobItemState.Done, i.State));
    }

    [Fact]
    public async Task Encode_svt_paralelo_config_antiga_sem_chave_fica_sequencial()
    {
        // JSON de config salva numa versão anterior (sem ParallelWorkers) tem que seguir
        // funcionando — e cair no caminho sequencial de sempre (regra de compatibilidade)
        await _settings.SetAsync("encode.cfg.svt_av1",
            """{"Code":"svt_av1","Preset":6,"Multipass":true}""");

        var job = NewJob("ep_seq.mkv");
        job.Items.Add(new JobItem
        {
            Order = 2, Title = "Parte 2", StartSeconds = 60, EndSeconds = 120,
            Class = BitrateClass.Episode, TargetKbps = 500,
        });
        await _jobs.AddAsync(job);

        var encode = new FakeEncode();
        var maxInFlight = 0;
        var inFlight = 0;
        encode.OnEncode = _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, now));
            Thread.Sleep(50);
            Interlocked.Decrement(ref inFlight);
        };

        var runner = NewRunner(encode);
        await runner.RunAsync();

        Assert.Equal(2, encode.EncodedSources.Count);
        Assert.Equal(1, maxInFlight); // nunca houve sobreposição
        Assert.Equal(JobState.Done, (await _jobs.GetAllOrderedAsync()).Single().State);
    }

    [Fact]
    public async Task Encode_paralelo_2_workers_soma_fps_dos_slots_no_rodape()
    {
        // O relato de progresso do paralelo tem que SOMAR os encodes em voo (2 slots × 10 fps
        // = 20 no rodapé) — era isso que ficava 0.0 no modo 2 workers antes do agregador.
        await _settings.SetAsync("encode.cfg.svt_av1",
            System.Text.Json.JsonSerializer.Serialize(new CodecEncodeConfig { Code = "svt_av1", ParallelWorkers = 2 }));

        var job = NewJob("ep_par_fps.mkv");
        job.Items.Add(new JobItem
        {
            Order = 2, Title = "Parte 2", StartSeconds = 60, EndSeconds = 120,
            Class = BitrateClass.Episode, TargetKbps = 500,
        });
        await _jobs.AddAsync(job);

        var encode = new FakeEncode { FpsReport = 10 };
        var inFlight = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        encode.OnEncode = _ =>
        {
            if (Interlocked.Increment(ref inFlight) == 2)
                bothStarted.TrySetResult();
            bothStarted.Task.Wait(3_000);
        };

        var runner = NewRunner(encode);
        var gate = new object();
        var maxFps = 0.0;
        runner.StatsUpdated += s =>
        {
            lock (gate)
                maxFps = Math.Max(maxFps, s.Fps);
        };

        await runner.RunAsync();

        // Progress<T> posta os callbacks assíncronos — espera o pico chegar
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (maxFps < 20 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            lock (gate) { _ = maxFps; }
        }
        Assert.True(maxFps >= 20, $"o rodapé nunca somou os 2 slots (fps máximo visto: {maxFps})");
        Assert.Equal(JobState.Done, (await _jobs.GetAllOrderedAsync()).Single().State);
    }

    [Fact]
    public async Task Capitulo_com_preset_e_cq_proprios_sobrescrevem_a_config_no_encode()
    {
        // override por capítulo (V0.42): Preset/Cq do JobItem vencem a config da aba Encodes
        var job = NewJob("ep_override.mkv");
        job.Items[0].Preset = 2;
        job.Items[0].Cq = 30;
        await _jobs.AddAsync(job);

        var encode = new FakeEncode();
        await NewRunner(encode).RunAsync();

        var seen = encode.SeenCfgs.Single();
        Assert.Equal(2, seen.Cfg.Preset);
        Assert.Equal(30, seen.Cfg.Cq);
        Assert.Equal(500, seen.Kbps); // bitrate da parte segue o de sempre
    }

    [Fact]
    public async Task Capitulo_sem_override_usa_a_config_do_codec_intacta()
    {
        var job = NewJob("ep_sem_override.mkv");
        await _jobs.AddAsync(job);

        var encode = new FakeEncode();
        await NewRunner(encode).RunAsync();

        var seen = encode.SeenCfgs.Single();
        Assert.Equal(6, seen.Cfg.Preset);  // default do svt_av1
        Assert.Equal(22, seen.Cfg.Cq);
    }

    [Fact]
    public async Task Rodape_paralelo_mostra_uma_linha_por_worker_com_o_bitrate_da_parte()
    {
        await _settings.SetAsync("encode.cfg.svt_av1",
            System.Text.Json.JsonSerializer.Serialize(new CodecEncodeConfig { Code = "svt_av1", ParallelWorkers = 2 }));

        var job = NewJob("ep_par_linhas.mkv");
        job.Items.Add(new JobItem
        {
            Order = 2, Title = "Parte 2", StartSeconds = 60, EndSeconds = 120,
            Class = BitrateClass.Episode, TargetKbps = 500,
        });
        await _jobs.AddAsync(job);

        var encode = new FakeEncode { FpsReport = 10 };
        var inFlight = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        encode.OnEncode = _ =>
        {
            if (Interlocked.Increment(ref inFlight) == 2)
                bothStarted.TrySetResult();
            bothStarted.Task.Wait(3_000);
        };

        var runner = NewRunner(encode);
        var gate = new object();
        var maxWorkers = 0;
        Dictionary<string, WorkerStat> lastByName = new();
        runner.StatsUpdated += s =>
        {
            lock (gate)
            {
                if (s.Workers is { } ws)
                {
                    maxWorkers = Math.Max(maxWorkers, ws.Length);
                    foreach (var w in ws)
                        lastByName[w.Title] = w;
                }
            }
        };

        await runner.RunAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (maxWorkers < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // as duas partes apareceram como linhas individuais, cada uma com o bitrate alvo
        lock (gate)
        {
            Assert.True(maxWorkers >= 2, $"nunca vi 2 workers no rodapé (máximo: {maxWorkers})");
            Assert.True(lastByName.ContainsKey("Parte 1"));
            Assert.True(lastByName.ContainsKey("Parte 2"));
            Assert.All(lastByName.Values, w => Assert.Equal("500 kbps", w.Rate));
        }
        Assert.Equal(JobState.Done, (await _jobs.GetAllOrderedAsync()).Single().State);
    }

    [Fact]
    public async Task Rodape_sequencial_mostra_o_bitrate_da_parte_em_curso()
    {
        var job = NewJob("ep_seq_rate.mkv");
        await _jobs.AddAsync(job);

        var runner = NewRunner(new FakeEncode());
        var gate = new object();
        var seenRate = "";
        runner.StatsUpdated += s =>
        {
            lock (gate)
                if (!string.IsNullOrEmpty(s.Rate))
                    seenRate = s.Rate;
        };

        await runner.RunAsync();

        lock (gate)
            Assert.Equal("500 kbps", seenRate);
    }

    [Fact]
    public async Task Rodape_carrega_o_contador_de_chunks_do_av1an()
    {
        var job = NewJob("ep_chunks.mkv");
        await _jobs.AddAsync(job);

        var encode = new FakeEncode { FpsReport = 10, ChunksDoneReport = 3, ChunksTotalReport = 56 };
        var runner = NewRunner(encode);
        var gate = new object();
        var (done, total) = (0, 0);
        runner.StatsUpdated += s =>
        {
            lock (gate)
                if (s.ChunksTotal > 0)
                    (done, total) = (s.ChunksDone, s.ChunksTotal);
        };

        await runner.RunAsync();

        lock (gate)
        {
            Assert.Equal(3, done);
            Assert.Equal(56, total);
        }
    }

    // ---- Fakes ----

    /// <summary>QC fake: grava os pedidos recebidos e devolve resultado fixo (ou lança, p/ falha).</summary>
    private sealed class FakeQualityCheck : IQualityCheckStage
    {
        public List<QualityCheckRequest> Requests = [];
        public QualityResult? Result { get; set; } = new(95.5, 0.995, 41.2);
        public Exception? Throw { get; set; }

        public async Task<QualityResult?> MeasureAsync(QualityCheckRequest req, CancellationToken ct)
        {
            await Task.Yield();
            if (Throw is not null)
                throw Throw;
            Requests.Add(req);
            return Result;
        }
    }

    [Fact]
    public async Task QC_ligada_medida_e_gravada_no_item()
    {
        var job = NewJob("ep_qc_on.mkv");
        await _jobs.AddAsync(job);
        await _settings.SetAsync(SettingsRepository.QueueQualityCheck, "true");

        var qc = new FakeQualityCheck();
        var runner = NewRunner(new FakeEncode(), quality: () => qc);
        await runner.RunAsync();

        var item = (await _jobs.GetAllOrderedAsync()).Single().Items.Single();
        Assert.Equal(95.5, item.QualityVmaf);
        Assert.Equal(0.995, item.QualitySsim);
        Assert.Equal(41.2, item.QualityPsnr);
        // Referência = trecho da ORIGEM (start/duração da parte); distorcida = parte encodeada
        var req = Assert.Single(qc.Requests);
        Assert.Equal(job.SourcePath, req.RefPath);
        Assert.Equal(0, req.RefStartSeconds);
        Assert.Equal(60, req.RefDurationSeconds);
        Assert.EndsWith(".mkv", req.DistPath);
    }

    [Fact]
    public async Task QC_desligada_por_padrao_nao_medida()
    {
        var job = NewJob("ep_qc_off.mkv");
        await _jobs.AddAsync(job);

        var qc = new FakeQualityCheck();
        var runner = NewRunner(new FakeEncode(), quality: () => qc);
        await runner.RunAsync();

        Assert.Empty(qc.Requests); // setting ausente = desligada
        var item = (await _jobs.GetAllOrderedAsync()).Single().Items.Single();
        Assert.Null(item.QualityVmaf);
    }

    [Fact]
    public async Task QC_falha_nao_derruba_o_job()
    {
        var job = NewJob("ep_qc_fail.mkv");
        await _jobs.AddAsync(job);
        await _settings.SetAsync(SettingsRepository.QueueQualityCheck, "true");

        var qc = new FakeQualityCheck { Throw = new InvalidOperationException("vmaf explodiu") };
        var runner = NewRunner(new FakeEncode(), quality: () => qc);
        await runner.RunAsync();

        var done = (await _jobs.GetAllOrderedAsync()).Single();
        Assert.Equal(JobState.Done, done.State);           // QC é best-effort
        Assert.Equal(JobItemState.Done, done.Items[0].State);
        Assert.Null(done.Items[0].QualityVmaf);
    }

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
        /// <summary>cudaGpu recebido por chamada (null = CPU, sem -gpu).</summary>
        public ConcurrentBag<int?> SeenGpus = [];

    /// <summary>Passadas a relatar via progress (simula encode multipass do SVT).</summary>
    public int[]? PassReports;

    /// <summary>FPS de cada relato de progresso (0 = não relata; usado no teste do rodapé
    /// somado do encode paralelo).</summary>
    public double FpsReport;

    /// <summary>Chunks (motor Av1an) incluídos em cada relato de progresso.</summary>
    public int ChunksDoneReport;
    public int ChunksTotalReport;

    /// <summary>Configs recebidas por chamada (com os overrides do capítulo aplicados).</summary>
    public ConcurrentBag<(CodecEncodeConfig Cfg, int Kbps)> SeenCfgs = [];

    public Task EncodePartAsync(
        string sourcePath, EncodeService.JobItemRef item, string outputPath, string passLogBase,
        CodecEncodeConfig cfg, int kbps, CancellationToken ct,
        IProgress<EncodeProgress>? progress, int? cudaGpu = null)
    {
        OnEncode?.Invoke(sourcePath);
        SeenCfgs.Add((cfg, kbps));
        if (FpsReport > 0 && progress is not null)
            progress.Report(new EncodeProgress(FpsReport, 1.0, 30,
                ChunksDone: ChunksDoneReport, ChunksTotal: ChunksTotalReport));
        if (PassReports is not null && progress is not null)
            foreach (var pr in PassReports)
                progress.Report(new EncodeProgress(10, 1.0, 30, pr));
            File.WriteAllText(outputPath, "parte fake");
            SeenGpus.Add(cudaGpu);
            lock (EncodedSources)          // o pool paralelo chama de threads diferentes
                EncodedSources.Add(sourcePath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMerge : IMergeStage
    {
        public int MergeCalls;
        /// <summary>Partes do último merge com a flag de "não vira capítulo" (Critical/temporário).</summary>
        public List<(string PartPath, bool IsCritical)> LastParts = [];

        public Task MergeAsync(
            IReadOnlyList<(string PartPath, string Title, double StartSeconds, double EndSeconds, bool IsCritical)> parts,
            string finalPath, string chaptersPath, CancellationToken ct)
        {
            MergeCalls++;
            LastParts = [.. parts.Select(p => (p.PartPath, p.IsCritical))];
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
