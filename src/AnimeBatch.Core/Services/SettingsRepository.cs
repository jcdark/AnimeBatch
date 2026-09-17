using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Services;

/// <summary>Preferências chave/valor no banco (ex.: "tmdb.apikey").</summary>
public class SettingsRepository(Func<AnimeBatchDbContext> contextFactory)
{
    public const string TmdbApiKey = "tmdb.apikey";
    public const string LanguageKey = "app.language";
    public const string OutputDirectory = "output.dir";
    public const string SourceDirectory = "source.dir";
    public const string QueueAutoRemove = "queue.autoRemove";
    public const string UpscaleGpus = "upscale.gpus";
    /// <summary>Minutos sem saída do ffmpeg antes de matar o processo por stall
    /// (número; vazio/inválido = 10). Lido pelo QueueRunner ao montar o EncodeService.</summary>
    public const string StallMinutes = "queue.stallMinutes";
    /// <summary>Pastas extras de busca de binários, separadas por ';' (ToolsLocator).</summary>
    public const string ToolsExtraDirs = "tools.extraDirs";

    private readonly Func<AnimeBatchDbContext> _factory = contextFactory;

    public async Task<string?> GetAsync(string key)
    {
        await using var db = _factory();
        return (await db.Settings.FirstOrDefaultAsync(s => s.Key == key).ConfigureAwait(false))?.Value;
    }

    public async Task SetAsync(string key, string value)
    {
        await using var db = _factory();
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key).ConfigureAwait(false);
        if (existing is null)
            db.Settings.Add(new Setting { Key = key, Value = value });
        else
            existing.Value = value;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
