using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Fila de jobs persistida. A ordem vive no banco (Job.Order) e sobrevive a reinícios;
/// o executável da fila (M2) sempre pega o menor Order pendente.
/// </summary>
public class JobRepository(Func<AnimeBatchDbContext> contextFactory)
{
    private readonly Func<AnimeBatchDbContext> _factory = contextFactory;

    public async Task<List<Job>> GetAllOrderedAsync()
    {
        await using var db = _factory();
        return await db.Jobs
            .Include(j => j.Items)
            .OrderBy(j => j.Order)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<int> NextOrderAsync()
    {
        await using var db = _factory();
        var max = await db.Jobs.MaxAsync(j => (int?)j.Order).ConfigureAwait(false);
        return (max ?? 0) + 1;
    }

    /// <summary>
    /// Enfileira um job. Se já existir job pendente/pausado/erro/cancelado para o MESMO
    /// arquivo de origem, sobrescreve as configurações e as partes (mantendo a posição na
    /// fila) em vez de duplicar. Jobs concluídos ou em execução não são sobrescritos.
    /// </summary>
    public async Task AddAsync(Job job)
    {
        await using var db = _factory();
        var overwriteStates = new[] { JobState.Pending, JobState.Paused, JobState.Error, JobState.Cancelled };
        var existing = await db.Jobs
            .Include(j => j.Items)
            .FirstOrDefaultAsync(j => j.SourcePath == job.SourcePath && overwriteStates.Contains(j.State))
            .ConfigureAwait(false);

        if (existing is null)
        {
            job.Order = await NextOrderAsync().ConfigureAwait(false);
            db.Jobs.Add(job);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return;
        }

        existing.SeriesName = job.SeriesName;
        existing.EpisodeKbps = job.EpisodeKbps;
        existing.OpeningKbps = job.OpeningKbps;
        existing.EndingKbps = job.EndingKbps;
        existing.UpscaleMode = job.UpscaleMode;
        existing.UpscaleModel = job.UpscaleModel;
        existing.UpscaleTargetHeight = job.UpscaleTargetHeight;
        existing.VideoCodec = job.VideoCodec;
        existing.State = JobState.Pending;
        existing.CreatedAt = job.CreatedAt;

        db.JobItems.RemoveRange(existing.Items);
        foreach (var item in job.Items)
        {
            item.JobId = existing.Id;
            db.JobItems.Add(item);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        job.Id = existing.Id;
        job.Order = existing.Order;
    }

    /// <summary>
    /// Move um job na fila: delta = -1 (sobe), +1 (desce), int.MinValue (vai pro topo).
    /// Troca de posição direta com o vizinho — o job precisa estar pendente/pausado.
    /// </summary>
    public async Task<bool> MoveAsync(int jobId, int delta)
    {
        await using var db = _factory();
        var all = await db.Jobs.OrderBy(j => j.Order).ToListAsync().ConfigureAwait(false);
        var index = all.FindIndex(j => j.Id == jobId);
        if (index < 0)
            return false;

        int target = delta == int.MinValue ? 0 : Math.Clamp(index + delta, 0, all.Count - 1);
        if (target == index)
            return false;

        // Renumera tudo deslocando os intermediários — evita colisão de Order único a único.
        var moved = all[index];
        all.RemoveAt(index);
        all.Insert(target, moved);
        for (var i = 0; i < all.Count; i++)
            all[i].Order = i + 1;

        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(int jobId)
    {
        await using var db = _factory();
        return await db.Jobs.Where(j => j.Id == jobId).ExecuteDeleteAsync().ConfigureAwait(false) > 0;
    }

    /// <summary>Atualiza as configurações de um job existente (codec, upscale, bitrates, etc.).</summary>
    public async Task<bool> UpdateAsync(Job job)
    {
        await using var db = _factory();
        var existing = await db.Jobs.FirstOrDefaultAsync(j => j.Id == job.Id).ConfigureAwait(false);
        if (existing is null)
            return false;

        existing.SeriesName = job.SeriesName;
        existing.EpisodeKbps = job.EpisodeKbps;
        existing.OpeningKbps = job.OpeningKbps;
        existing.EndingKbps = job.EndingKbps;
        existing.UpscaleMode = job.UpscaleMode;
        existing.UpscaleModel = job.UpscaleModel;
        existing.UpscaleTargetHeight = job.UpscaleTargetHeight;
        existing.VideoCodec = job.VideoCodec;

        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Muda o estado de um job; errorMessage grava/limpa o motivo do erro (null limpa).</summary>
    public async Task SetStateAsync(int jobId, JobState state, string? errorMessage = null)
    {
        await using var db = _factory();
        await db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, state)
                .SetProperty(j => j.ErrorMessage, errorMessage))
            .ConfigureAwait(false);
    }

    /// <summary>Limpa a fila: remove todos os jobs, exceto o que está em execução agora.</summary>
    public async Task<int> ClearAllAsync()
    {
        await using var db = _factory();
        return await db.Jobs
            .Where(j => j.State != JobState.Running)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Reprocessa os jobs em erro (chamado quando o usuário clica em iniciar).</summary>
    public async Task<int> ResetErrorJobsAsync()
    {
        await using var db = _factory();
        return await db.Jobs
            .Where(j => j.State == JobState.Error)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Pending)
                .SetProperty(j => j.ErrorMessage, (string?)null))
            .ConfigureAwait(false);
    }

    /// <summary>Retoma os jobs pausados (chamado quando o usuário clica em iniciar).</summary>
    public async Task<int> ResetPausedJobsAsync()
    {
        await using var db = _factory();
        return await db.Jobs
            .Where(j => j.State == JobState.Paused)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Pending)
                .SetProperty(j => j.ErrorMessage, (string?)null))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Devolve jobs Running pra fila — sobrou de um app morto/crashado no meio do
    /// processamento (sem isso o job ficaria Running pra sempre e a fila o ignoraria).
    /// </summary>
    public async Task<int> ResetRunningJobsAsync()
    {
        await using var db = _factory();
        return await db.Jobs
            .Where(j => j.State == JobState.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Pending)
                .SetProperty(j => j.ErrorMessage, (string?)null))
            .ConfigureAwait(false);
    }

    /// <summary>Muda o estado de um item (e opcionalmente grava o caminho de saída).</summary>
    public async Task SetItemStateAsync(int itemId, JobItemState state, string? outputPath = null)
    {
        await using var db = _factory();
        await db.JobItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => outputPath != null
                ? s.SetProperty(i => i.State, state).SetProperty(i => i.OutputPath, outputPath)
                : s.SetProperty(i => i.State, state))
            .ConfigureAwait(false);
    }

    /// <summary>Grava o resultado da QC de qualidade (VMAF/SSIM/PSNR) de uma parte.</summary>
    public async Task SetItemQualityAsync(int itemId, double vmaf, double ssim, double psnr)
    {
        await using var db = _factory();
        await db.JobItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.QualityVmaf, vmaf)
                .SetProperty(i => i.QualitySsim, ssim)
                .SetProperty(i => i.QualityPsnr, psnr))
            .ConfigureAwait(false);
    }

    /// <summary>Próximo job a executar (menor Order pendente) ou null se a fila estiver vazia/pausada.</summary>
    public async Task<Job?> PeekNextPendingAsync()
    {
        await using var db = _factory();
        return await db.Jobs
            .Include(j => j.Items)
            .Where(j => j.State == JobState.Pending)
            .OrderBy(j => j.Order)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }
}
