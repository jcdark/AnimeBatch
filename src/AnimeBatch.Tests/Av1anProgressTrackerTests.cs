using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Unit do rastreador de progresso do motor Av1an: consome linhas REAIS do logfile
/// (formato capturado de um encode 2-pass de verdade) e devolve fps/velocidade/tempo
/// convertido. O relógio é injetado para o cálculo ser determinístico.
/// </summary>
public class Av1anProgressTrackerTests
{
    // Linhas copiadas de um logfile real (-l) de encode com -p 2 (o prefixo de timestamp
    // existe nas linhas de chunk; a linha "Queue ..." começa limpa no arquivo)
    private const string LinhaQueue = "Queue 165 Workers 16 Encoder svt-av1 Passes 2";
    private const string LinhaStarted =
        "2026-09-22T21:28:54.610381Z DEBUG encode_chunk{worker_id=0 total_chunks=165 chunk_index=\"00000\"}: av1an_core::broker:  started chunk 00000: 240 frames";
    private const string LinhaFinished =
        "2026-09-22T21:28:57.454691Z DEBUG encode_chunk{worker_id=1 total_chunks=165 chunk_index=\"00001\"}: av1an_core::broker: finished chunk 00001: 240 frames, 53.10 fps, took 2.84s";
    private const string LinhaQualquer =
        "2026-09-22T21:21:19.002461Z  INFO encode_file: av1an_core::scenes: scenecut: found 163 scene(s) [with extra_splits (240 frames): 165 scene(s)]";

    [Fact]
    public void Antes_do_primeiro_chunk_snapshot_eh_null()
    {
        var t = new Av1anProgressTracker(() => 1_000);
        t.FeedLine(LinhaQueue);
        t.FeedLine(LinhaQualquer);
        Assert.Null(t.Snapshot(600));
    }

    [Fact]
    public void Chunk_iniciado_sem_concluido_nao_relata()
    {
        // Sem nenhum "finished chunk" não há fps mensurável (o gate é o primeiro chunk
        // concluído, não o relógio — encodes rápidos seguem relatando)
        var t = new Av1anProgressTracker(() => 60_000);
        t.FeedLine(LinhaQueue);
        t.FeedLine(LinhaStarted);
        Assert.Null(t.Snapshot(600));
    }

    [Fact]
    public void Fps_e_velocidade_sao_medias_desde_o_primeiro_chunk()
    {
        var ticks = 10_000L;
        var t = new Av1anProgressTracker(() => ticks);
        t.FeedLine(LinhaQueue);
        t.FeedLine(LinhaStarted);         // início do relógio do encode
        ticks = 20_000;                   // 10s depois
        Assert.True(t.FeedLine(LinhaFinished), "\"finished chunk\" deve pedir relato imediato");

        // 240 frames em 10s = 24 fps; 1/165 dos chunks × 660s = 4s convertidos → 0.4x
        var p = t.Snapshot(660);
        Assert.NotNull(p);
        Assert.Equal(24.0, p!.Fps, 3);
        Assert.Equal(4.0, p!.OutTimeSeconds, 3);
        Assert.Equal(0.4, p!.Speed, 3);
    }

    [Fact]
    public void Linhas_sem_queue_dao_fps_mas_velocidade_zero()
    {
        // robustez: se o formato da linha "Queue ..." mudar, fps segue vivo
        var ticks = 10_000L;
        var t = new Av1anProgressTracker(() => ticks);
        t.FeedLine(LinhaStarted);
        ticks = 60_000;
        t.FeedLine(LinhaFinished);

        var p = t.Snapshot(600);
        Assert.NotNull(p);
        Assert.Equal(4.8, p!.Fps, 3);      // 240 frames / 50s
        Assert.Equal(0, p!.Speed);
        Assert.Equal(0, p!.OutTimeSeconds);
    }

    [Fact]
    public void Chunk_iniciado_segundo_nao_conta_frames_do_primeiro()
    {
        // Chunks em voo não viram progresso: 1 concluído de 2 → metade, e o fps
        // só soma o frames do chunk que terminou
        var ticks = 10_000L;
        var t = new Av1anProgressTracker(() => ticks);
        t.FeedLine(LinhaQueue.Replace("165", "2"));
        t.FeedLine(LinhaStarted);
        t.FeedLine(LinhaStarted.Replace("00000", "00001"));
        ticks = 30_000;
        t.FeedLine(LinhaFinished);

        var p = t.Snapshot(600);
        Assert.NotNull(p);
        Assert.Equal(12.0, p!.Fps, 3);         // 240 frames concluídos / 20s
        Assert.Equal(300, p!.OutTimeSeconds, 3); // 1/2 × 600s
        Assert.Equal(15.0, p!.Speed, 3);       // 300s / 20s
    }

    [Fact]
    public void OutTime_nunca_passa_da_duracao()
    {
        var ticks = 10_000L;
        var t = new Av1anProgressTracker(() => ticks);
        t.FeedLine(LinhaQueue.Replace("165", "1")); // total 1
        t.FeedLine(LinhaStarted);
        ticks = 20_000;
        for (var i = 0; i < 3; i++)                 // 3 "concluídos" num total de 1 (defensivo)
            t.FeedLine(LinhaFinished);

        var p = t.Snapshot(300);
        Assert.NotNull(p);
        Assert.Equal(300, p!.OutTimeSeconds, 3);
    }

    [Fact]
    public void Linhas_vazias_e_sem_match_nao_relato_imediato()
    {
        var t = new Av1anProgressTracker(() => 1_000);
        Assert.False(t.FeedLine(null));
        Assert.False(t.FeedLine(""));
        Assert.False(t.FeedLine(LinhaQualquer));
        Assert.False(t.FeedLine(LinhaQueue));
        Assert.False(t.FeedLine(LinhaStarted));
    }

    [Fact]
    public void Snapshot_carrega_o_contador_de_chunks()
    {
        var ticks = 10_000L;
        var t = new Av1anProgressTracker(() => ticks);
        t.FeedLine(LinhaQueue);           // 165 chunks no total
        t.FeedLine(LinhaStarted);
        ticks = 20_000;
        t.FeedLine(LinhaFinished);

        var p = t.Snapshot(660);
        Assert.NotNull(p);
        Assert.Equal(1, p!.ChunksDone);
        Assert.Equal(165, p!.ChunksTotal);
        Assert.Equal("chunks", p!.Phase);
    }

    [Fact]
    public void Heartbeat_mostra_a_fase_antes_do_primeiro_chunk()
    {
        // sequência REAL do logfile: análise de cenas é a fase inicial (silenciosa — só o
        // resumo "scenecut: found" aparece), depois a preparação/segmentação e os chunks
        var t = new Av1anProgressTracker(() => 1_000);
        Assert.Equal("scenes", t.Heartbeat().Phase);
        t.FeedLine(LinhaQueue);
        Assert.Equal("scenes", t.Heartbeat().Phase);

        // o resumo ENCERRA a análise — o que vem depois é a montagem dos chunks (fase
        // "preparing"): sem isso o rodapé ficava "analisando cenas" durante a segmentação
        t.FeedLine("2026-09-22T21:22:26.752921Z  INFO encode_file: av1an_core::scenes: scenecut: found 54 scene(s) [with extra_splits (240 frames): 56 scene(s)]");
        var hb = t.Heartbeat();
        Assert.Equal(0, hb.Fps);
        Assert.Equal(0, hb.ChunksDone);
        Assert.Equal(165, hb.ChunksTotal); // o total vem da linha "Queue 165 Workers 16"
        Assert.Equal("preparing", hb.Phase);

        t.FeedLine("2026-09-23T01:26:31.586092Z DEBUG encode_file: av1an_core::context: Segmenting video");
        Assert.Equal("segmenting", t.Heartbeat().Phase);

        // primeiro chunk iniciado: fase vira chunks (mesmo sem nenhum concluído)
        t.FeedLine(LinhaStarted);
        Assert.Equal("chunks", t.Heartbeat().Phase);
    }
}
