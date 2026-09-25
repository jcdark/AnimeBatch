using AnimeBatch.Core.Data;
using AnimeBatch.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Persistência de séries: foco na capa em base64 (V0.41) — gravação no banco e
/// sobrevivência ao Upsert de bitrate/nome (a edição da série NÃO pode perder a capa).
/// </summary>
public class SeriesRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"animebatch-series-{Guid.NewGuid():N}.db");
    private readonly SeriesRepository _repo;

    public SeriesRepositoryTests()
    {
        using var db = new AnimeBatchDbContext(_dbPath);
        db.Database.EnsureCreated();
        _repo = new SeriesRepository(() => new AnimeBatchDbContext(_dbPath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task Capa_base64_persiste_no_banco()
    {
        var saved = await _repo.UpsertAsync(new Core.Models.Series
        { Name = "Kaiju Girl", EpisodeKbps = 500, OpeningKbps = 1500, EndingKbps = 500 });

        Assert.True(await _repo.UpdateCoverAsync(saved.Id, "aGVsbG8="));

        var loaded = await _repo.FindByNormalizedNameAsync("kaiju girl");
        Assert.NotNull(loaded);
        Assert.Equal("aGVsbG8=", loaded.CoverImageBase64);
    }

    [Fact]
    public async Task Upsert_da_serie_nao_perde_a_capa()
    {
        var saved = await _repo.UpsertAsync(new Core.Models.Series
        { Name = "Magilumiere", EpisodeKbps = 500, OpeningKbps = 1500, EndingKbps = 500 });
        await _repo.UpdateCoverAsync(saved.Id, "Y2FwYQ==");

        // dono edita os bitrates na aba Séries — a capa gravada tem que continuar lá
        await _repo.UpsertAsync(new Core.Models.Series
        { Name = "Magilumiere", EpisodeKbps = 800, OpeningKbps = 2000, EndingKbps = 700 });

        var loaded = await _repo.FindByNormalizedNameAsync("magilumiere");
        Assert.NotNull(loaded);
        Assert.Equal(800, loaded.EpisodeKbps);
        Assert.Equal("Y2FwYQ==", loaded.CoverImageBase64);
    }

    [Fact]
    public async Task Cover_null_para_serie_sem_imagem()
    {
        await _repo.UpsertAsync(new Core.Models.Series { Name = "Sem Capa" });
        var loaded = await _repo.FindByNormalizedNameAsync("sem capa");
        Assert.NotNull(loaded);
        Assert.Null(loaded.CoverImageBase64);
    }
}
