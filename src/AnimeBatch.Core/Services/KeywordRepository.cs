using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Services;

/// <summary>CRUD das palavras-chave de classificação OP/ED (editáveis na UI).</summary>
public class KeywordRepository(Func<AnimeBatchDbContext> contextFactory)
{
    private readonly Func<AnimeBatchDbContext> _factory = contextFactory;

    public async Task<List<KeywordRule>> GetAllAsync()
    {
        await using var db = _factory();
        return await db.Keywords.OrderBy(k => k.Category).ThenBy(k => k.Word).ToListAsync().ConfigureAwait(false);
    }

    public async Task<KeywordRule> UpsertAsync(KeywordRule rule)
    {
        rule.Word = rule.Word.Trim().ToLowerInvariant();
        if (rule.Word.Length == 0)
            throw new ArgumentException("Palavra-chave vazia.", nameof(rule));

        await using var db = _factory();
        var existing = await db.Keywords.FirstOrDefaultAsync(k =>
            k.Category == rule.Category && k.Word == rule.Word).ConfigureAwait(false);
        if (existing is null)
        {
            db.Keywords.Add(rule);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return rule;
        }

        return existing;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = _factory();
        return await db.Keywords.Where(k => k.Id == id).ExecuteDeleteAsync().ConfigureAwait(false) > 0;
    }
}
