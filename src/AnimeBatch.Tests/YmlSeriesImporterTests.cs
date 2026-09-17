using AnimeBatch.Core.Data;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class YmlSeriesImporterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SeriesRepository _repo;

    public YmlSeriesImporterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"animebatch-test-{Guid.NewGuid():N}.db");
        // No app as migrations rodam no startup; no teste, cria o schema direto.
        using var db = new AnimeBatchDbContext(_dbPath);
        db.Database.EnsureCreated();
        _repo = new SeriesRepository(() => new AnimeBatchDbContext(_dbPath));
    }

    public void Dispose()
    {
        // O pool de conexões do SQLite mantém o arquivo aberto; solta antes de apagar.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private const string ConverterYml = """
        - name: "BLACK TORCH"
          data:
            min: 500
            max: 1800
            end: 900

        - name: "Clevatess"
          data:
            min: 500
            max: 2200
            end: 900

        - name: "Welcome to Demon School! Iruma-kun"
          data:
            min: 5050
            max: 2500
            end: 1700

        - name: "Welcome to Demon School! Iruma-kun"
          data:
            min: 550
            max: 2500
            end: 1700
        """;

    [Fact]
    public void Parse_le_entradas_do_formato_do_script()
    {
        var entries = YmlSeriesImporter.Parse(ConverterYml);

        Assert.Equal(4, entries.Count);
        Assert.Equal("BLACK TORCH", entries[0].Name);
        Assert.Equal(1800, entries[0].Data!.Max);
        Assert.Equal(500, entries[0].Data!.Min);
    }

    [Fact]
    public async Task ImportAsync_importa_e_duplicada_primeira_vence()
    {
        var importer = new YmlSeriesImporter(() => new AnimeBatchDbContext(_dbPath));

        var report = await importer.ImportAsync(ConverterYml);

        Assert.Equal(3, report.Imported);
        Assert.Single(report.Skipped);
        Assert.Contains("duplicada", report.Skipped[0].Reason);

        // Iruma-kun: primeira entrada vence (min 5050, o typo do YML real)
        var iruma = await _repo.FindByEpisodeAsync("Welcome to Demon School! Iruma-kun - S04E21.mkv");
        Assert.NotNull(iruma);
        Assert.Equal(5050, iruma.EpisodeKbps);
        Assert.Equal(1700, iruma.EndingKbps);

        var torch = await _repo.FindByEpisodeAsync("BLACK TORCH - S01E10.mkv");
        Assert.NotNull(torch);
        Assert.Equal("black torch", torch.NormalizedName);
        Assert.Equal(900, torch.EndingKbps);
    }

    [Fact]
    public async Task ImportAsync_reimport_atualiza_em_vez_de_duplicar()
    {
        var importer = new YmlSeriesImporter(() => new AnimeBatchDbContext(_dbPath));
        await importer.ImportAsync(ConverterYml);

        var report2 = await importer.ImportAsync(ConverterYml);

        Assert.Equal(0, report2.Imported);
        Assert.Equal(3, report2.Updated);
        var all = await _repo.GetAllAsync();
        Assert.Equal(3, all.Count);
    }
}
