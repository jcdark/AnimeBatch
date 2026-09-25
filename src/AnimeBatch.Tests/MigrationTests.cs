using AnimeBatch.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Exercita o caminho REAL de upgrade do banco (Database.Migrate), que nenhuma outra
/// suíte cobria — os testes de repositório usam EnsureCreated, que sempre cria o schema
/// atual. O teste de upgrade recria à mão o schema v1 (pré-rename de colunas) e roda as
/// migrations 2–4 por cima, validando que os dados sobrevivem às renomeações semânticas.
/// </summary>
public class MigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"animebatch-mig-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private List<string> ColumnsOf(string table)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read())
            cols.Add(r.GetString(0));
        return cols;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    [Fact]
    public async Task Migrate_em_banco_vazio_cria_schema_atual()
    {
        await using var db = new AnimeBatchDbContext(_dbPath);
        await db.Database.MigrateAsync();

        var jobs = ColumnsOf("Jobs");
        Assert.Contains("EpisodeKbps", jobs);
        Assert.Contains("OpeningKbps", jobs);
        Assert.Contains("EndingKbps", jobs);
        Assert.Contains("UpscaleMode", jobs);
        Assert.Contains("VideoCodec", jobs);
        Assert.Contains("ErrorMessage", jobs);
        Assert.Contains("IsTemporary", ColumnsOf("JobItems")); // V0.41 (capítulo temporário)
        Assert.Contains("Preset", ColumnsOf("JobItems"));      // V0.42 (preset por capítulo)
        Assert.Contains("Cq", ColumnsOf("JobItems"));          // V0.42 (CQ por capítulo)
        Assert.DoesNotContain("MinKbps", jobs);         // renomeado
        Assert.DoesNotContain("UpscaleEnabled", jobs);  // virou UpscaleMode

        var series = ColumnsOf("Series");
        Assert.Contains("TmdbId", series);
        Assert.Contains("CoverImageBase64", series);    // V0.41 (capa em base64)
        Assert.Contains("FileName", ColumnsOf("ConversionRecords")); // nasceu na 2ª migration
    }

    [Fact]
    public async Task Migrate_do_schema_v1_preserva_dados_e_adiciona_colunas()
    {
        // Schema v1 (InitialCreate): Jobs/Series com MinKbps/MaxKbps/EndKbps e
        // UpscaleOptionsJson; SEM ConversionRecords, UpscaleMode, VideoCodec, ErrorMessage.
        // A linha no __EFMigrationsHistory marca a 1ª migration como aplicada, então o
        // Migrate() só roda as 3 seguintes (rename → mode/codec → error message).
        using (var conn = Open())
        {
            var ddl = """
                CREATE TABLE "Jobs" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "Order" INTEGER NOT NULL,
                    "SourcePath" TEXT NOT NULL,
                    "SeriesName" TEXT,
                    "MinKbps" INTEGER NOT NULL,
                    "MaxKbps" INTEGER NOT NULL,
                    "EndKbps" INTEGER NOT NULL,
                    "State" INTEGER NOT NULL,
                    "UpscaleOptionsJson" TEXT,
                    "CreatedAt" TEXT NOT NULL);
                CREATE TABLE "Series" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "NormalizedName" TEXT NOT NULL,
                    "MinKbps" INTEGER NOT NULL,
                    "MaxKbps" INTEGER NOT NULL,
                    "EndKbps" INTEGER NOT NULL);
                CREATE TABLE "JobItems" (
                    "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "JobId" INTEGER NOT NULL,
                    "Order" INTEGER NOT NULL,
                    "Title" TEXT NOT NULL,
                    "StartSeconds" REAL NOT NULL,
                    "EndSeconds" REAL NOT NULL,
                    "Class" INTEGER NOT NULL,
                    "TargetKbps" INTEGER NOT NULL,
                    "State" INTEGER NOT NULL,
                    "OutputPath" TEXT NULL);
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL);
                INSERT INTO "Jobs" ("Order", "SourcePath", "SeriesName", "MinKbps", "MaxKbps", "EndKbps", "State", "CreatedAt")
                    VALUES (1, 'G:\Dublados\ep01.mkv', 'Serie Antiga', 500, 1500, 900, 0, '2026-09-15 03:00:00');
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260915025006_InitialCreate', '9.0.0');
                """;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }

        await using var db = new AnimeBatchDbContext(_dbPath);
        await db.Database.MigrateAsync();

        var jobs = ColumnsOf("Jobs");
        Assert.Contains("EpisodeKbps", jobs);
        Assert.Contains("OpeningKbps", jobs);
        Assert.Contains("EndingKbps", jobs);
        Assert.Contains("UpscaleMode", jobs);
        Assert.Contains("VideoCodec", jobs);
        Assert.Contains("ErrorMessage", jobs);
        Assert.Contains("IsTemporary", ColumnsOf("JobItems")); // V0.41 (capítulo temporário)
        Assert.Contains("Preset", ColumnsOf("JobItems"));      // V0.42 (preset por capítulo)
        Assert.Contains("Cq", ColumnsOf("JobItems"));          // V0.42 (CQ por capítulo)
        Assert.DoesNotContain("UpscaleOptionsJson", jobs); // dropada na 2ª migration
        Assert.Contains("CoverImageBase64", ColumnsOf("Series")); // V0.41 (capa em base64)
        Assert.Contains("FileName", ColumnsOf("ConversionRecords"));

        // os bitrates sobreviveram ao rename SEMÂNTICO (o risco real do upgrade em produção)
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT \"EpisodeKbps\", \"OpeningKbps\", \"EndingKbps\" FROM \"Jobs\" WHERE \"SourcePath\" = 'G:\\Dublados\\ep01.mkv'";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read(), "job enfileirado no schema v1 sumiu no upgrade");
            Assert.Equal(500, r.GetInt64(0));
            Assert.Equal(1500, r.GetInt64(1));
            Assert.Equal(900, r.GetInt64(2));
        }
    }
}
