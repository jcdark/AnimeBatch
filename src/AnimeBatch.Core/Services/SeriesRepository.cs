using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Services;

/// <summary>CRUD de séries/bitrates no banco local + busca por episódio.</summary>
public class SeriesRepository(Func<AnimeBatchDbContext> contextFactory)
{
    private readonly Func<AnimeBatchDbContext> _factory = contextFactory;

    public async Task<List<Series>> GetAllAsync()
    {
        await using var db = _factory();
        return await db.Series.OrderBy(s => s.NormalizedName).ToListAsync().ConfigureAwait(false);
    }

    /// <summary>Encontra a série de um episódio pelo nome normalizado (mesma regra do script).</summary>
    public async Task<Series?> FindByEpisodeAsync(string episodeFileName)
    {
        var normalized = ChapterService.CleanSeriesName(episodeFileName);
        return await FindByNormalizedNameAsync(normalized).ConfigureAwait(false);
    }

    public async Task<Series?> FindByNormalizedNameAsync(string normalizedName)
    {
        await using var db = _factory();
        return await db.Series.FirstOrDefaultAsync(s => s.NormalizedName == normalizedName).ConfigureAwait(false);
    }

    /// <summary>Insere ou atualiza (pela chave normalizada) uma série.</summary>
    public async Task<Series> UpsertAsync(Series series)
    {
        series.NormalizedName = ChapterService.CleanSeriesName(series.Name);
        if (string.IsNullOrEmpty(series.NormalizedName))
            throw new ArgumentException("Nome da série não pode ficar vazio.", nameof(series));

        await using var db = _factory();
        var existing = await db.Series.FirstOrDefaultAsync(s => s.NormalizedName == series.NormalizedName).ConfigureAwait(false);
        if (existing is null)
        {
            db.Series.Add(series);
        }
        else
        {
            existing.Name = series.Name;
            existing.EpisodeKbps = series.EpisodeKbps;
            existing.OpeningKbps = series.OpeningKbps;
            existing.EndingKbps = series.EndingKbps;
            series = existing;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return series;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = _factory();
        var affected = await db.Series.Where(s => s.Id == id).ExecuteDeleteAsync().ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Vincula os dados do TMDB (id, poster, sinopse) a uma série.</summary>
    public async Task<bool> UpdateTmdbAsync(int id, int tmdbId, string? posterPath, string? overview)
    {
        await using var db = _factory();
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id).ConfigureAwait(false);
        if (series is null)
            return false;

        series.TmdbId = tmdbId;
        series.PosterPath = posterPath;
        series.Overview = overview;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Grava a capa da série em base64 (null = sem capa). O download do TMDB é
    /// salvo no banco, não em arquivo de cache — sobrevive a reinstalação.</summary>
    public async Task<bool> UpdateCoverAsync(int id, string? coverImageBase64)
    {
        await using var db = _factory();
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id).ConfigureAwait(false);
        if (series is null)
            return false;

        series.CoverImageBase64 = coverImageBase64;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }
}
