using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Write-back do kbps do capítulo para o padrão da série (V0.64): ajuste fino no modal
/// vira o default da próxima encode. Temporário, cena crítica e valor igual/inválido não escrevem.
/// </summary>
public class SeriesKbpsSyncTests
{
    private static Series Serie() => new()
    { Name = "Black Torch", EpisodeKbps = 500, OpeningKbps = 1500, EndingKbps = 500 };

    [Fact]
    public void Abertura_com_valor_novo_propoe_write_back()
    {
        var r = SeriesKbpsSync.Compute(BitrateClass.Opening, 1800, Serie(), isTemporary: false);
        Assert.NotNull(r);
        Assert.Equal((null, 1800, null), r.Value);
    }

    [Fact]
    public void Encerramento_com_valor_novo_propoe_write_back()
    {
        var r = SeriesKbpsSync.Compute(BitrateClass.Ending, 700, Serie(), isTemporary: false);
        Assert.NotNull(r);
        Assert.Equal((null, null, 700), r.Value);
    }

    [Fact]
    public void Episodio_com_valor_novo_propoe_write_back()
    {
        var r = SeriesKbpsSync.Compute(BitrateClass.Episode, 600, Serie(), isTemporary: false);
        Assert.NotNull(r);
        Assert.Equal((600, null, null), r.Value);
    }

    [Fact]
    public void Valor_igual_ao_atual_nao_propoe()
    {
        Assert.Null(SeriesKbpsSync.Compute(BitrateClass.Opening, 1500, Serie(), isTemporary: false));
        Assert.Null(SeriesKbpsSync.Compute(BitrateClass.Episode, 500, Serie(), isTemporary: false));
    }

    [Fact]
    public void Capitulo_temporario_nunca_escreve()
    {
        Assert.Null(SeriesKbpsSync.Compute(BitrateClass.Opening, 1800, Serie(), isTemporary: true));
    }

    [Fact]
    public void Cena_critica_nunca_escreve()
    {
        // cena crítica usa o bitrate da abertura, mas é pontual — não é a abertura da série
        Assert.Null(SeriesKbpsSync.Compute(BitrateClass.Critical, 1800, Serie(), isTemporary: false));
    }

    [Fact]
    public void Kbps_invalido_nao_escreve()
    {
        Assert.Null(SeriesKbpsSync.Compute(BitrateClass.Opening, 0, Serie(), isTemporary: false));
    }
}
