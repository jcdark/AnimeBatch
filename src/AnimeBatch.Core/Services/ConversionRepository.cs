using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Services;

/// <summary>Histórico de arquivos convertidos (tela de Séries → "Arquivos Convertidos").</summary>
public class ConversionRepository(Func<AnimeBatchDbContext> contextFactory)
{
    private readonly Func<AnimeBatchDbContext> _factory = contextFactory;

    public async Task AddAsync(ConversionRecord record)
    {
        await using var db = _factory();
        db.ConversionRecords.Add(record);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Histórico de uma série; seriesId null devolve todo o histórico.</summary>
    public async Task<List<ConversionRecord>> GetBySeriesAsync(int? seriesId)
    {
        await using var db = _factory();
        var query = db.ConversionRecords.AsQueryable();
        if (seriesId is not null)
            query = query.Where(c => c.SeriesId == seriesId);
        return await query.OrderByDescending(c => c.ConvertedAt).ToListAsync().ConfigureAwait(false);
    }
}
