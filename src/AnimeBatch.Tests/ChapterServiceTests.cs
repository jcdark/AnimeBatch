using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class ChapterServiceTests
{
    [Theory]
    [InlineData("BLACK TORCH - S01E10.mkv", "black torch")]
    [InlineData("Clevatess - S02E09.mkv", "clevatess")]
    [InlineData("Trapped in a Dating Sim： The World of Otome Games Is Tough for Mobs - S02E06.mkv",
        "trapped in a dating sim： the world of otome games is tough for mobs")]
    [InlineData("The 100 Girlfriends Who Really, Really, Really, Really, REALLY Love You - S03E31.mkv",
        "the 100 girlfriends who really, really, really, really, really love you")]
    [InlineData("Filme Sem Sufixo.mkv", "filme sem sufixo")]
    [InlineData("Serie - s12e345 extras.mp4", "serie")]
    public void CleanSeriesName_remove_sufixo_e_normaliza(string filename, string expected)
    {
        Assert.Equal(expected, ChapterService.CleanSeriesName(filename));
    }

    [Fact]
    public void Classify_abertura_tem_precedencia_sobre_encerramento()
    {
        // "opened" casa tanto com abertura ("opened") quanto com encerramento ("ed") — abertura vence.
        var result = ChapterService.Classify("Opened", ["op", "opening", "opened"], ["ed", "end", "credits"]);
        Assert.Equal(BitrateClass.Opening, result);
    }

    [Theory]
    [InlineData("OP", BitrateClass.Opening)]
    [InlineData("Intro", BitrateClass.Opening)]
    [InlineData("Abertura", BitrateClass.Opening)]
    [InlineData("ED", BitrateClass.Ending)]
    [InlineData("Credits", BitrateClass.Ending)]
    [InlineData("Ending", BitrateClass.Ending)]
    [InlineData("Parte 1", BitrateClass.Episode)]
    public void Classifica_por_palavra_chave_do_titulo(string title, BitrateClass expected)
    {
        var op = new[] { "op", "opening", "opened", "abertura", "open", "intro" };
        var end = new[] { "ed", "end", "ending", "finalização", "finalizacao", "credits" };
        Assert.Equal(expected, ChapterService.Classify(title, op, end));
    }

    [Theory]
    [InlineData("Crítico")]
    [InlineData("critico")]
    [InlineData("Critical scene")]
    public void Cena_critica_tem_precedencia_e_vira_classe_critical(string title)
    {
        var op = new[] { "op", "opening", "opened", "abertura", "open", "intro" };
        var end = new[] { "ed", "end", "ending", "finalização", "finalizacao", "credits" };
        Assert.Equal(BitrateClass.Critical, ChapterService.Classify(title, op, end));
    }

    [Fact]
    public void Cena_critica_vence_ate_abertura()
    {
        var op = new[] { "op", "opening", "opened", "abertura", "open", "intro" };
        var end = new[] { "ed", "end", "ending", "finalização", "finalizacao", "credits" };
        Assert.Equal(BitrateClass.Critical, ChapterService.Classify("Critical OP", op, end));
    }

    [Fact]
    public void BuildRanges_usa_proximo_capitulo_menos_epsilon_e_ultimo_vai_ate_a_duracao()
    {
        var chapters = new List<ChapterInfo>
        {
            new(1, "Episode", 0.0, 36.0),
            new(2, "Intro", 36.0, 111.0),
            new(3, "Episode", 111.0, 1433.62),
        };
        const double duration = 1433.62;

        var ranges = new ChapterService().BuildRanges(chapters, duration);

        Assert.Equal(3, ranges.Count);
        Assert.Equal(0.0, ranges[0].StartSeconds);
        Assert.Equal(36.0 - 0.001, ranges[0].EndSeconds, 3);
        Assert.Equal(111.0 - 0.001, ranges[1].EndSeconds, 3);
        Assert.Equal(duration, ranges[2].EndSeconds, 3);
    }

    [Fact]
    public void BuildRanges_descarta_capitulos_muito_curtos()
    {
        var chapters = new List<ChapterInfo>
        {
            new(1, "Title card", 0.0, 0.2),      // 0.2s < 0.5s → fora
            new(2, "Parte 1", 0.2, 600.0),
            new(3, "Parte 2", 600.0, 1200.0),
        };

        var ranges = new ChapterService().BuildRanges(chapters, 1200.0);

        Assert.Equal(2, ranges.Count);
        Assert.Equal("Parte 1", ranges[0].Title);
        Assert.Equal("Parte 2", ranges[1].Title);
    }

    [Fact]
    public void BuildRanges_arquivo_sem_capitulos_gera_capitulo_default_ate_a_duracao()
    {
        var ranges = new ChapterService().BuildRanges([], 1433.62);

        var range = Assert.Single(ranges);
        Assert.Equal(0.0, range.StartSeconds);
        Assert.Equal(1433.62, range.EndSeconds, 3);
        Assert.Equal("Capitulo 1", range.Title);
    }

    [Fact]
    public void BuildRanges_todos_os_capitulos_curtos_gera_default_cobrindo_o_arquivo()
    {
        var chapters = new List<ChapterInfo> { new(1, "Blink", 0.0, 0.2) };

        var ranges = new ChapterService().BuildRanges(chapters, 600.0);

        var range = Assert.Single(ranges);
        Assert.Equal(0.0, range.StartSeconds);
        Assert.Equal(600.0, range.EndSeconds, 3);
    }

    [Fact]
    public void BuildRanges_duracao_zero_nao_gera_default()
    {
        Assert.Empty(new ChapterService().BuildRanges([], 0));
    }

    [Fact]
    public void BuildRanges_capitulo_sem_titulo_recebe_nome_padrao()
    {
        var chapters = new List<ChapterInfo> { new(1, "", 0.0, 100.0) };

        var ranges = new ChapterService().BuildRanges(chapters, 100.0);

        Assert.Equal("Capitulo 1", ranges[0].Title);
    }

    [Fact]
    public void SanitizeTitle_remove_caracteres_invalidos_e_limita_60()
    {
        Assert.Equal("OP _intro", ChapterService.SanitizeTitle("OP: _intro?"));
        var longo = new string('a', 100);
        Assert.Equal(60, ChapterService.SanitizeTitle(longo).Length);
        Assert.Equal("chapter", ChapterService.SanitizeTitle(new string(Path.GetInvalidFileNameChars())));
    }

    [Fact]
    public void BuildPartFileName_segue_formato_do_script()
    {
        Assert.Equal("2 - Parte 1 - Clevatess - S02E09.mkv",
            ChapterService.BuildPartFileName(2, "Parte 1", "Clevatess - S02E09"));
    }
}
