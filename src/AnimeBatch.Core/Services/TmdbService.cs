using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AnimeBatch.Core.Services;

public record TmdbSearchResult(int Id, string Name, string? FirstAirYear, string? PosterPath, string Overview);

/// <summary>
/// Cliente da API v3 do TMDB (themoviedb.org). Requer chave de API configurada pelo usuário
/// (Configurações → chave fica no banco local, Setting "tmdb.apikey").
/// Busca e sinopse em pt-BR.
/// </summary>
public class TmdbService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public TmdbService(string apiKey) : this(apiKey, new HttpClient()) { }

    public TmdbService(string apiKey, HttpClient http)
    {
        _apiKey = apiKey;
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeBatch/0.2");
    }

    /// <summary>Nomes vindos de arquivos têm "_" no lugar de ":" (ex.: "Magilumiere_ Companhia")
    /// e o sublinhado quebra a busca do TMDB; trocar por espaço volta a casar com o título.</summary>
    public static string CleanSearchQuery(string query) =>
        string.Join(' ', (query ?? "").Replace('_', ' ').Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Busca séries por nome. Retorna nome, ano de estreia, poster e sinopse em pt-BR.</summary>
    public async Task<List<TmdbSearchResult>> SearchTvAsync(string query, CancellationToken ct = default)
    {
        query = CleanSearchQuery(query);
        var url = $"https://api.themoviedb.org/3/search/tv?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=pt-BR";
        using var doc = await JsonDocument.ParseAsync(
            await _http.GetStreamAsync(url, ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        var results = new List<TmdbSearchResult>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var year = r.TryGetProperty("first_air_date", out var d) && d.ValueKind == JsonValueKind.String
                ? (d.GetString() ?? "") is { Length: >= 4 } s ? s[..4] : null
                : null;
            results.Add(new TmdbSearchResult(
                r.GetProperty("id").GetInt32(),
                r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                year,
                r.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                r.TryGetProperty("overview", out var o) ? o.GetString() ?? "" : ""));
        }

        return results;
    }

    /// <summary>Busca uma série pelo ID do TMDB (/tv/{id}); null se o ID não existe.
    /// Usado quando a busca por nome não encontra (títulos alternativos/romaji).</summary>
    public async Task<TmdbSearchResult?> GetTvAsync(int id, CancellationToken ct = default)
    {
        var url = $"https://api.themoviedb.org/3/tv/{id}?api_key={_apiKey}&language=pt-BR";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null; // 404 = ID inexistente
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var year = r.TryGetProperty("first_air_date", out var d) && d.ValueKind == JsonValueKind.String
            ? (d.GetString() ?? "") is { Length: >= 4 } s ? s[..4] : null
            : null;
        return new TmdbSearchResult(
            r.GetProperty("id").GetInt32(),
            r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            year,
            r.TryGetProperty("poster_path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
            r.TryGetProperty("overview", out var o) ? o.GetString() ?? "" : "");
    }

    /// <summary>Baixa o poster (JPG) no tamanho indicado (w342 é bom pra exibição).</summary>
    public async Task<byte[]> DownloadPosterAsync(string posterPath, string size = "w342", CancellationToken ct = default) =>
        await _http.GetByteArrayAsync(PosterUrl(posterPath, size), ct).ConfigureAwait(false);

    public static string PosterUrl(string posterPath, string size = "w342") =>
        $"https://image.tmdb.org/t/p/{size}{posterPath}";
}
