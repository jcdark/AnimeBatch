using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeBatch.Tests;

public class JobRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JobRepository _jobs;

    public JobRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"animebatch-jobs-{Guid.NewGuid():N}.db");
        using var db = new AnimeBatchDbContext(_dbPath);
        db.Database.EnsureCreated();
        _jobs = new JobRepository(() => new AnimeBatchDbContext(_dbPath));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static Job NewJob(string sourcePath, int endingKbps = 900) => new()
    {
        SourcePath = sourcePath,
        SeriesName = "Serie Teste",
        EpisodeKbps = 500,
        OpeningKbps = 1500,
        EndingKbps = endingKbps,
        Items =
        {
            new JobItem { Order = 1, Title = "Parte 1", StartSeconds = 0, EndSeconds = 60, Class = BitrateClass.Episode, TargetKbps = 500 },
            new JobItem { Order = 2, Title = "ED", StartSeconds = 60, EndSeconds = 120, Class = BitrateClass.Ending, TargetKbps = endingKbps },
        },
    };

    [Fact]
    public async Task AddAsync_mesmo_arquivo_sobrescreve_em_vez_de_duplicar()
    {
        await _jobs.AddAsync(NewJob(@"E:\x\ep1.mkv", endingKbps: 900));

        var v2 = NewJob(@"E:\x\ep1.mkv", endingKbps: 2500);
        v2.UpscaleMode = UpscaleMode.WithEncode;
        v2.VideoCodec = "nvenc_av1_10bit";
        await _jobs.AddAsync(v2);

        var all = await _jobs.GetAllOrderedAsync();
        var job = Assert.Single(all);
        Assert.Equal(2500, job.EndingKbps);
        Assert.Equal(UpscaleMode.WithEncode, job.UpscaleMode);
        Assert.Equal("nvenc_av1_10bit", job.VideoCodec);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(2, job.Items.Count);
    }

    [Fact]
    public async Task AddAsync_arquivos_diferentes_nao_sobrescrevem()
    {
        await _jobs.AddAsync(NewJob(@"E:\x\ep1.mkv"));
        await _jobs.AddAsync(NewJob(@"E:\x\ep2.mkv"));

        var all = await _jobs.GetAllOrderedAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(1, all[0].Order);
        Assert.Equal(2, all[1].Order);
    }

    [Fact]
    public async Task Job_concluido_nao_e_sobrescrito()
    {
        var job = NewJob(@"E:\x\ep1.mkv");
        await _jobs.AddAsync(job);
        using (var db = new AnimeBatchDbContext(_dbPath))
        {
            var tracked = await db.Jobs.FirstAsync(j => j.SourcePath == job.SourcePath);
            tracked.State = JobState.Done;
            await db.SaveChangesAsync();
        }

        await _jobs.AddAsync(NewJob(@"E:\x\ep1.mkv", endingKbps: 42));

        var all = await _jobs.GetAllOrderedAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, j => j.EndingKbps == 900 && j.State == JobState.Done);
    }

    [Fact]
    public async Task PeekNextPendingAsync_retorna_menor_order_pendente()
    {
        await _jobs.AddAsync(NewJob(@"E:\x\ep1.mkv"));
        await _jobs.AddAsync(NewJob(@"E:\x\ep2.mkv"));

        var next = await _jobs.PeekNextPendingAsync();
        Assert.NotNull(next);
        Assert.Equal(@"E:\x\ep1.mkv", next.SourcePath);
    }
}
