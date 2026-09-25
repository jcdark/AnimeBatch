using System.Net;
using System.Text;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class TmdbServiceTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private const string TvDetailJson =
        """{"id":30983,"name":"Black Cat","first_air_date":"2005-10-06","poster_path":"/abc.jpg","overview":"Sinopse pt-BR"}""";

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

    [Fact]
    public async Task Busca_por_id_mapeia_o_detalhe_da_serie()
    {
        string? requested = null;
        var http = new HttpClient(new FakeHandler(req =>
        {
            requested = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TvDetailJson, Encoding.UTF8, "application/json"),
            };
        }));

        var result = await new TmdbService("key", http).GetTvAsync(30983);

        Assert.NotNull(result);
        Assert.Equal(30983, result!.Id);
        Assert.Equal("Black Cat", result.Name);
        Assert.Equal("2005", result.FirstAirYear);
        Assert.Equal("/abc.jpg", result.PosterPath);
        Assert.Equal("Sinopse pt-BR", result.Overview);
        Assert.Contains("/tv/30983", requested);
        Assert.Contains("language=pt-BR", requested);
    }

    [Fact]
    public async Task Busca_por_id_inexistente_retorna_null()
    {
        var http = new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await new TmdbService("key", http).GetTvAsync(999999999);

        Assert.Null(result);
    }
}
