using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Arquivos de capítulos editados (%LOCALAPPDATA%\AnimeBatch\chapters-edits): grade salva
/// ao adicionar/editar capítulo VENCE os capítulos do vídeo ao reabrir o episódio.
/// </summary>
public class ChapterEditsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"animebatch-edits-{Guid.NewGuid():N}");
    private readonly ChapterEditsStore _store;

    public ChapterEditsStoreTests() => _store = new ChapterEditsStore(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ChapterEditRecord Rec(int n, string title = "Intro", bool temp = false, bool include = true) =>
        new(n, title, StartSeconds: n * 60.0, EndSeconds: n * 60.0 + 50.5,
            Class: ChapterEditsStore.FormatClass(BitrateClass.Opening), TargetKbps: 1500,
            Include: include, IsTemporary: temp);

    [Fact]
    public void Salvar_e_recarregar_preserva_a_grade_completa()
    {
        var src = @"G:\Dublados\KAIJU GIRL - S01E08.mkv";
        _store.Save(src, [Rec(1), Rec(2, "Cap 2", temp: true, include: false)]);

        var loaded = _store.Load(src);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Intro", loaded[0].Title);
        Assert.Equal(60.0, loaded[0].StartSeconds, 3);
        Assert.Equal(110.5, loaded[0].EndSeconds, 3);
        Assert.Equal(BitrateClass.Opening, ChapterEditsStore.ParseClass(loaded[0].Class));
        Assert.Equal(1500, loaded[0].TargetKbps);
        Assert.True(loaded[0].Include);
        Assert.False(loaded[0].IsTemporary);
        Assert.False(loaded[1].Include);       // desmarcado persiste
        Assert.True(loaded[1].IsTemporary);    // temporário persiste
    }

    [Fact]
    public void PathFor_eh_estavel_e_diferente_por_pasta()
    {
        var a = _store.PathFor(@"G:\Dublados\ep.mkv");
        var a2 = _store.PathFor(@"G:\Dublados\ep.mkv");
        var b = _store.PathFor(@"G:\Legendados\ep.mkv"); // mesmo nome, pasta diferente
        Assert.Equal(a, a2);
        Assert.NotEqual(a, b);
        Assert.StartsWith(_dir, a);
        Assert.EndsWith(".json", a);
    }

    [Fact]
    public void ExistsFor_e_Delete_fecham_o_ciclo_do_reset()
    {
        var src = @"G:\Dublados\ep.mkv";
        Assert.False(_store.ExistsFor(src));
        Assert.Null(_store.Load(src));

        _store.Save(src, [Rec(1)]);
        Assert.True(_store.ExistsFor(src));

        Assert.True(_store.Delete(src));
        Assert.False(_store.ExistsFor(src));
        Assert.Null(_store.Load(src));
        Assert.False(_store.Delete(src)); // segunda vez: nada a apagar
    }

    [Fact]
    public void Json_corrompido_cai_em_sem_edicao_em_vez_de_derrubar()
    {
        var src = @"G:\Dublados\ep.mkv";
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.PathFor(src), "{ isso não é json");
        Assert.Null(_store.Load(src));
    }

    [Fact]
    public void Classe_desconhecida_volta_para_episode()
    {
        Assert.Equal(BitrateClass.Episode, ChapterEditsStore.ParseClass("QueSeja"));
        Assert.Equal(BitrateClass.Opening, ChapterEditsStore.ParseClass("opening"));
        Assert.Equal(BitrateClass.Ending, ChapterEditsStore.ParseClass("Ending"));
        Assert.Equal(BitrateClass.Critical, ChapterEditsStore.ParseClass("Critical"));
        Assert.Equal(BitrateClass.Episode, ChapterEditsStore.ParseClass(null));
    }

    [Fact]
    public void Preset_e_cq_do_capitulo_sobrevivem_ao_json()
    {
        var src = @"G:\Dublados\ep.mkv";
        _store.Save(src, [new ChapterEditRecord(1, "Intro", 0, 60, "Episode", 500, true, false, Preset: 2, Cq: 30)]);

        var loaded = _store.Load(src);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded[0].Preset);
        Assert.Equal(30, loaded[0].Cq);
    }

    [Fact]
    public void Json_legado_sem_preset_e_cq_carrega_com_zero_usa_config()
    {
        var src = @"G:\Dublados\ep.mkv";
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.PathFor(src), """
            {
              "SourceFile": "G:\\Dublados\\ep.mkv",
              "Chapters": [
                { "Number": 1, "Title": "Intro", "StartSeconds": 0, "EndSeconds": 60,
                  "Class": "Episode", "TargetKbps": 500, "Include": true }
              ]
            }
            """);

        var loaded = _store.Load(src);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded[0].Preset); // 0 = não informado → usa a config do codec
        Assert.Equal(0, loaded[0].Cq);
    }
}
