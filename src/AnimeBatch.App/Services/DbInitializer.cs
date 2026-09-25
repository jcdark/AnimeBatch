using AnimeBatch.Core.Data;
using AnimeBatch.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AnimeBatch.App.Services;

/// <summary>
/// Abre o banco em %LOCALAPPDATA%\AnimeBatch: na primeira execução copia para lá o banco
/// legado (data\animebatch.db da pasta do app — vem no pacote e é a base de quem já usava
/// versões antigas); depois aplica migrations; semeia keywords padrão e importa o
/// converter.yml legado (embutido em seed\) quando o banco de séries está vazio.
/// </summary>
internal static class DbInitializer
{
    public static void Initialize(Func<AnimeBatchDbContext> factory)
    {
        CarryOverLegacyDatabase();

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

    /// <summary>Primeira execução com o banco novo em %LOCALAPPDATA%: se existir um
    /// animebatch.db na data\ da pasta do app (pacote portátil de versão anterior),
    /// copia para o destino novo — nada dos dados do usuário se perde na migração de
    /// local. NUNCA sobrescreve um banco já existente no AppData (ele é a fonte da
    /// verdade a partir daí; o data\ do pacote vira só seed de instalação nova).</summary>
    private static void CarryOverLegacyDatabase()
    {
        try
        {
            var target = AppDataPaths.DbPath;
            if (File.Exists(target))
                return;

            var legacy = Path.Combine(AppContext.BaseDirectory, "data", "animebatch.db");
            if (File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(legacy, target);
            }
        }
        catch
        {
            // sem o carry-over o app começa com base nova — nunca derruba o boot por isso
        }
    }
}
