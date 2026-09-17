using AnimeBatch.Core.Data;
using AnimeBatch.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.App.Services;

/// <summary>
/// Abre o banco na pasta do app: aplica migrations; semeia keywords padrão e importa o
/// converter.yml legado (embutido em seed\) quando o banco de séries está vazio.
/// </summary>
internal static class DbInitializer
{
    public static void Initialize(Func<AnimeBatchDbContext> factory)
    {
        using var db = factory();
        db.EnsureDatabaseFolder();

        if (db.Database.GetMigrations().Any())
            db.Database.Migrate();
        else
            db.Database.EnsureCreated();

        if (!db.Keywords.Any())
        {
            db.Keywords.AddRange(ChapterService.DefaultKeywords());
            db.SaveChanges();
        }

        // Importação automática do YML legado como modelo inicial (uma única vez: só
        // acontece com a tabela de séries vazia — depois o banco é a fonte da verdade).
        if (!db.Series.Any())
        {
            var seedPath = Path.Combine(AppContext.BaseDirectory, "seed", "converter.yml");
            if (File.Exists(seedPath))
            {
                var importer = new YmlSeriesImporter(factory);
                importer.ImportAsync(File.ReadAllText(seedPath)).GetAwaiter().GetResult();
            }
        }
    }
}
