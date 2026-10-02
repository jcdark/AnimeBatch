using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Services;

/// <summary>Preferências chave/valor no banco (ex.: "tmdb.apikey").</summary>
public class SettingsRepository(Func<AnimeBatchDbContext> contextFactory)
{
    /// <summary>Chave da API do TMDB no banco de configurações (o VALOR gravado é
    /// "tmdb.apikey" — renomear a string do banco exigiria migração das instalações).</summary>
    public const string TmdbKeySetting = "tmdb.apikey";
    public const string LanguageKey = "app.language";
    public const string OutputDirectory = "output.dir";
    public const string SourceDirectory = "source.dir";
    public const string QueueAutoRemove = "queue.autoRemove";
    /// <summary>Ação "quando terminar" da fila: none/shutdown/hibernate/sleep/logoff/lock/exit.</summary>
    public const string QueueWhenDone = "queue.whenDone";
    public const string UpscaleGpus = "upscale.gpus";
    /// <summary>Minutos sem saída do ffmpeg antes de matar o processo por stall
    /// (número; vazio/inválido = 10). Lido pelo QueueRunner ao montar o EncodeService.</summary>
    public const string StallMinutes = "queue.stallMinutes";
    /// <summary>Pastas extras de busca de binários, separadas por ';' (ToolsLocator).</summary>
    public const string ToolsExtraDirs = "tools.extraDirs";
    /// <summary>Última pasta aberta na tela de Episódios — recarregada ao voltar à aba.</summary>
    public const string EpisodesLastFolder = "episodes.lastFolder";
    /// <summary>QC de qualidade opcional: mede VMAF/SSIM/PSNR (libvmaf) de cada parte contra a
    /// origem após o encode e grava no item. "true" = ligada; ausente = desligada (default).</summary>
    public const string QueueQualityCheck = "queue.qualityCheck";
    /// <summary>Bitrates (kbps) da Calibragem Automática por nível de criticidade. Ausente/vazio/≤0
    /// em QUALQUER uma = calibragem bloqueada até o dono definir os valores na tela de Configurações.</summary>
    public const string CalibrationVeryLowKbps = "calibracao.muitoBaixo";
    public const string CalibrationLowKbps = "calibracao.baixo";
    public const string CalibrationNormalKbps = "calibracao.normal";
    public const string CalibrationHighKbps = "calibracao.alto";
    public const string CalibrationVeryHighKbps = "calibracao.muitoAlto";

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
