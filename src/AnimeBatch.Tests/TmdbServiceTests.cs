using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class TmdbServiceTests
{
    [Theory]
    [InlineData(
        "Magilumiere_ Companhia Das Garotas Mágicas",
        "Magilumiere Companhia Das Garotas Mágicas")] // "_" (substituto de ":" no arquivo) quebra a busca
    [InlineData("Magilumiere:  Companhia   das Garotas", "Magilumiere: Companhia das Garotas")]
    [InlineData("  009-1  ", "009-1")]
    [InlineData("", "")]
    public void Limpa_query_de_busca(string input, string expected)
    {
        Assert.Equal(expected, TmdbService.CleanSearchQuery(input));
    }

    [Fact]
    public void Query_nula_nao_estoura()
    {
        Assert.Equal("", TmdbService.CleanSearchQuery(null!));
    }
}
