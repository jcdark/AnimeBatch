using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.Core.Data;

/// <summary>
/// Banco SQLite embutido, gravado dentro da pasta do próprio aplicativo
/// (AnimeBatch\data\animebatch.db): 100% local e portátil — mover a pasta do app leva os dados junto.
/// </summary>
public class AnimeBatchDbContext : DbContext
{
    /// <summary>Resolve o caminho do banco: junto do executável, em data\.</summary>
    public static string DefaultDbPath() =>
        Path.Combine(AppContext.BaseDirectory, "data", "animebatch.db");

    public string DbPath { get; }

    public DbSet<Series> Series => Set<Series>();
    public DbSet<KeywordRule> Keywords => Set<KeywordRule>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobItem> JobItems => Set<JobItem>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ConversionRecord> ConversionRecords => Set<ConversionRecord>();

    public AnimeBatchDbContext() : this(DefaultDbPath())
    {
    }

    public AnimeBatchDbContext(string dbPath)
    {
        DbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlite($"Data Source={DbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Series>(e =>
        {
            e.HasIndex(s => s.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<KeywordRule>(e =>
        {
            e.HasIndex(k => new { k.Category, k.Word }).IsUnique();
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.HasIndex(j => j.Order);
        });

        modelBuilder.Entity<JobItem>(e =>
        {
            e.HasOne(i => i.Job)
             .WithMany(j => j.Items)
             .HasForeignKey(i => i.JobId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.JobId, i.Order });
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
        });

        modelBuilder.Entity<ConversionRecord>(e =>
        {
            e.HasIndex(c => c.SeriesId);
        });
    }

    /// <summary>Cria a pasta do banco se não existir (chamar antes de migrar/abrir).</summary>
    public void EnsureDatabaseFolder()
    {
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
