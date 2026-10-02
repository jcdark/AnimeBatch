using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;

namespace AnimeBatch.Tests;

/// <summary>Ciclo de persistência da Calibragem Automática (calibracoes\{stem}-{hash8}.json) —
/// mesmo contrato do ChapterEditsStore: save sobrescreve, load tolera JSON corrompido, delete some.</summary>
public class CalibrationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"animebatch-calib-tests-{Guid.NewGuid():N}");
    private readonly CalibrationStore _store;

    public CalibrationStoreTests() => _store = new CalibrationStore(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Save_load_e_delete_no_ciclo_completo()
    {
        var source = Path.Combine(@"G:\Dublados", "Serie - S01E01.mkv");
        Assert.False(_store.ExistsFor(source));
        Assert.Null(_store.Load(source));

        var blocks = new List<CalibrationBlockRecord>
        {
            new(1, "VeryLow", 0, 15, 300, true),
            new(2, "VeryHigh", 15, 22.5, 2400, true, 4, 0),
            new(3, "Normal", 22.5, 30, 1000, false),
        };
        _store.Save(source, 1000, blocks);

        Assert.True(_store.ExistsFor(source));
        var data = _store.Load(source);
        Assert.NotNull(data);
        Assert.Equal(1000, data!.AnalysisKbps);
        Assert.Equal(3, data.Blocks.Count);
        Assert.Equal(Path.GetFullPath(source), data.SourceFile);

        Assert.Equal(CalibrationLevel.VeryLow, CalibrationStore.ParseLevel(data.Blocks[0].Level));
        Assert.Equal(CalibrationLevel.VeryHigh, CalibrationStore.ParseLevel(data.Blocks[1].Level));
        Assert.Equal(2400, data.Blocks[1].TargetKbps);
        Assert.Equal(4, data.Blocks[1].Preset);
        Assert.False(data.Blocks[2].Include); // desmarcado sobrevive ao round-trip

        Assert.True(_store.Delete(source));
        Assert.False(_store.ExistsFor(source));
        Assert.False(_store.Delete(source)); // segunda vez não tem o que apagar
    }

    [Fact]
    public void Load_de_json_corrompido_devolve_null_em_vez_de_derrubar()
    {
        var source = Path.Combine(@"G:\Dublados", "Serie - S01E02.mkv");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.PathFor(source), "{ json quebrado");

        Assert.Null(_store.Load(source));
    }

    [Fact]
    public void Load_de_json_sem_blocks_devolve_null()
    {
        var source = Path.Combine(@"G:\Dublados", "Serie - S01E03.mkv");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.PathFor(source), """{ "SourceFile": "x" }""");

        Assert.Null(_store.Load(source));
    }

    [Fact]
    public void Mesmo_video_em_pastas_diferentes_nao_colide()
    {
        var a = Path.Combine(@"G:\Dublados", "Serie - S01E01.mkv");
        var b = Path.Combine(@"G:\Outros", "Serie - S01E01.mkv");
        _store.Save(a, 1000, [new CalibrationBlockRecord(1, "Low", 0, 5, 600, true)]);

        Assert.True(_store.ExistsFor(a));
        Assert.False(_store.ExistsFor(b)); // hash do caminho completo diferencia
    }

    [Fact]
    public void ParseLevel_de_texto_estranho_cai_no_normal()
    {
        Assert.Equal(CalibrationLevel.High, CalibrationStore.ParseLevel("High"));
        Assert.Equal(CalibrationLevel.VeryLow, CalibrationStore.ParseLevel("verylow")); // case-insensitive
        Assert.Equal(CalibrationLevel.Normal, CalibrationStore.ParseLevel("lixo"));
        Assert.Equal(CalibrationLevel.Normal, CalibrationStore.ParseLevel(null));
        Assert.Equal("VeryHigh", CalibrationStore.FormatLevel(CalibrationLevel.VeryHigh));
    }
}
