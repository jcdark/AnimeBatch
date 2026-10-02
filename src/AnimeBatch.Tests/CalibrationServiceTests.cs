using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;

namespace AnimeBatch.Tests;

/// <summary>Pipeline puro da Calibragem Automática: derivação dos 5 níveis a partir do
/// consumo medido por janela, classificação nas bordas e mesclagem dos blocos.</summary>
public class CalibrationServiceTests
{
    // ---------- DeriveThresholds ----------

    [Fact]
    public void DeriveThresholds_divide_o_intervalo_min_max_em_5_faixas()
    {
        // 10 janelas: consumo linear 100..1000 → min=100, max=1000, intervalo=900
        var values = Enumerable.Range(0, 10).Select(i => 100.0 + i * 100).ToList();
        var t = CalibrationService.DeriveThresholds(values);

        Assert.Equal(100, t.Min);
        Assert.Equal(1000, t.Max);
        // limites superiores a 20%, 40%, 60% e 80% do intervalo — a média de min e max
        // (550) é exatamente o CENTRO da faixa Normal, como definido pelo dono
        Assert.Equal(280, t.UpperBounds[0]);
        Assert.Equal(460, t.UpperBounds[1]);
        Assert.Equal(640, t.UpperBounds[2]);
        Assert.Equal(820, t.UpperBounds[3]);
    }

    [Fact]
    public void DeriveThresholds_com_tudo_igual_degrada_para_normal()
    {
        var t = CalibrationService.DeriveThresholds([500, 500, 500]);
        Assert.Equal(CalibrationLevel.Normal, CalibrationService.Classify(t, 500));
        Assert.Equal(CalibrationLevel.Normal, CalibrationService.Classify(t, 9999));
    }

    // ---------- Classify ----------

    [Fact]
    public void Classify_encaixa_nas_5_faixas_inclusive_nas_bordas()
    {
        var t = CalibrationService.DeriveThresholds([0, 1000]); // min=0, max=1000, passos de 200
        Assert.Equal(CalibrationLevel.VeryLow, CalibrationService.Classify(t, 0));
        Assert.Equal(CalibrationLevel.VeryLow, CalibrationService.Classify(t, 199.9));
        Assert.Equal(CalibrationLevel.Low, CalibrationService.Classify(t, 200));
        Assert.Equal(CalibrationLevel.Normal, CalibrationService.Classify(t, 400));
        Assert.Equal(CalibrationLevel.Normal, CalibrationService.Classify(t, 500)); // a média min/max
        Assert.Equal(CalibrationLevel.High, CalibrationService.Classify(t, 600));
        Assert.Equal(CalibrationLevel.High, CalibrationService.Classify(t, 799.9));
        Assert.Equal(CalibrationLevel.VeryHigh, CalibrationService.Classify(t, 800));
        Assert.Equal(CalibrationLevel.VeryHigh, CalibrationService.Classify(t, 5000)); // acima do medido
    }

    // ---------- BuildWindows ----------

    [Fact]
    public void BuildWindows_cobre_a_duracao_toda_com_janelas_de_5s()
    {
        var kbps = Enumerable.Repeat(500.0, 5).ToList();
        var windows = CalibrationService.BuildWindows(23, kbps); // 23s → janelas 0-5,5-10,10-15,15-20,20-23
        Assert.Equal(5, windows.Count);
        Assert.Equal(0, windows[0].Start);
        Assert.Equal(20, windows[^2].End); // penúltima fecha exatamente na divisão de 20s
        Assert.Equal(23, windows[^1].End); // última corta na duração
        Assert.Equal(3, windows[^1].End - windows[^1].Start);
    }

    [Fact]
    public void BuildWindows_video_mais_curto_que_a_janela_gera_uma_so()
    {
        var windows = CalibrationService.BuildWindows(2, [100]);
        var block = Assert.Single(windows);
        Assert.Equal(0, block.Start);
        Assert.Equal(2, block.End);
    }

    // ---------- BuildBlocks ----------

    [Fact]
    public void BuildBlocks_mescla_janelas_adjacentes_do_mesmo_nivel()
    {
        // 6 janelas de 5s: |fraco|fraco|fraco|forte|fraco|fraco|
        var kbps = new[] { 100, 120, 90, 950, 110, 95 }.Select(v => (double)v).ToList();
        var windows = CalibrationService.BuildWindows(30, kbps);
        var blocks = CalibrationService.BuildBlocks(windows);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(CalibrationLevel.VeryLow, blocks[0].Level);
        Assert.Equal(0, blocks[0].StartSeconds);
        Assert.Equal(15, blocks[0].EndSeconds); // 3 janelas mescladas
        Assert.Equal(CalibrationLevel.VeryHigh, blocks[1].Level);
        Assert.Equal(15, blocks[1].StartSeconds);
        Assert.Equal(20, blocks[1].EndSeconds);
        Assert.Equal(CalibrationLevel.VeryLow, blocks[2].Level);
        Assert.Equal(30, blocks[^1].EndSeconds); // cobre o vídeo até o fim

        // renumeração 1..N contígua
        Assert.Equal([1, 2, 3], blocks.Select(b => b.Number));
    }

    [Fact]
    public void BuildBlocks_com_niveis_alternando_gera_um_bloco_por_mudanca()
    {
        // faixas de [100..1000]: <280 VeryLow, <460 Low, <640 Normal, <820 High, resto VeryHigh
        var kbps = new[] { 100, 400, 700, 1000, 100, 1000 }.Select(v => (double)v).ToList();
        var windows = CalibrationService.BuildWindows(30, kbps);
        var blocks = CalibrationService.BuildBlocks(windows);

        Assert.Equal(
            [CalibrationLevel.VeryLow, CalibrationLevel.Low, CalibrationLevel.High, CalibrationLevel.VeryHigh, CalibrationLevel.VeryLow, CalibrationLevel.VeryHigh],
            blocks.Select(b => b.Level));
    }

    // ---------- ParseWindowKbps (ffprobe de packets) ----------

    [Fact]
    public void ParseWindowKbps_soma_os_pacotes_por_janela()
    {
        // 3 pacotes na janela 0 (0-5s) somando 1250 bytes → 1250*8/5/1000 = 2 kbps
        // 1 pacote na janela 1 com 5000 bytes → 5000*8/5/1000 = 8 kbps
        // 1 pacote na janela 2 (10-15s) com 100 bytes → 0,16 kbps; janela 3 (15-20s) sem nada → 0
        const string json = """
            {
              "packets": [
                { "pts_time": "0.0",   "size": 400 },
                { "pts_time": "2.5",   "size": 600 },
                { "pts_time": "4.9",   "size": 250 },
                { "pts_time": "7.0",   "size": 5000 },
                { "pts_time": "12.0",  "size": 100 }
              ]
            }
            """;
        var kbps = CalibrationService.ParseWindowKbps(json, 20);

        Assert.Equal(4, kbps.Count); // 20s / 5s
        Assert.Equal(2.0, kbps[0], precision: 6);
        Assert.Equal(8.0, kbps[1], precision: 6);
        Assert.Equal(0.16, kbps[2], precision: 6);
        Assert.Equal(0.0, kbps[3]);
    }

    [Fact]
    public void ParseWindowKbps_sem_packets_devolve_lista_vazia()
    {
        Assert.Empty(CalibrationService.ParseWindowKbps("""{ "packets": [] }""", 30));
    }

    // ---------- snapshot de capítulos finais (MergeService) ----------

    [Fact]
    public void FinalChapters_json_vai_e_volta_sem_perda()
    {
        var original = new List<FinalChapter>
        {
            new(1, "Episode", 0),
            new(2, "Opening", 55.93),
            new(3, "Episode", 145.936),
        };
        var json = MergeService.BuildFinalChaptersJson(original);
        var parsed = MergeService.ParseFinalChapters(json);

        Assert.NotNull(parsed);
        Assert.Equal(original.Count, parsed!.Count);
        Assert.Equal([1, 2, 3], parsed.Select(c => c.Number));
        Assert.Equal(["Episode", "Opening", "Episode"], parsed.Select(c => c.Title));
        Assert.Equal([0, 55.93, 145.936], parsed.Select(c => c.StartSeconds));
    }

    [Fact]
    public void ParseFinalChapters_tolerante_a_lixo()
    {
        Assert.Null(MergeService.ParseFinalChapters(null));
        Assert.Null(MergeService.ParseFinalChapters(""));
        Assert.Null(MergeService.ParseFinalChapters("não é json"));
        Assert.Null(MergeService.ParseFinalChapters("""{"não":"é array"}"""));
        var parsed = MergeService.ParseFinalChapters("""[{"t":"Sem número e sem tempo"}]""");
        Assert.NotNull(parsed);
        _ = Assert.Single(parsed);
    }

    [Fact]
    public void BuildFinalChaptersTxt_escreve_ogm_com_tempos_absolutos_ordenados()
    {
        var txt = MergeService.BuildFinalChaptersTxt(
        [
            new FinalChapter(2, "Opening", 55.93),
            new FinalChapter(1, "Episode", 0),
        ]);

        var lines = txt.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            "CHAPTER01=00:00:00.000", "CHAPTER01NAME=Episode",
            "CHAPTER02=00:00:55.930", "CHAPTER02NAME=Opening",
        ], lines);
    }
}
