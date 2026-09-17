using AnimeBatch.Core.Data;
using AnimeBatch.Core.Models;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AnimeBatch.Core.Services;

public record YmlImportReport(int Imported, int Updated, List<(string Name, string Reason)> Skipped);

/// <summary>
/// Importa o converter.yml legado (formato do script: lista de name/data{min,max,end}) para o
/// banco. Roda uma única vez como migração; o YML deixa de existir em runtime.
/// Em caso de nome duplicado no arquivo (ex.: Iruma-kun), a PRIMEIRA entrada vence — mesmo
/// comportamento do script, que percorre a lista do começo ao fim.
/// </summary>
public class YmlSeriesImporter
{
    public class YmlEntry
    {
        public string Name { get; set; } = "";
        public YmlData? Data { get; set; }
    }

    public class YmlData
    {
        public int Min { get; set; }
        public int Max { get; set; }
        public int End { get; set; }
    }

    private readonly Func<AnimeBatchDbContext> _factory;

    public YmlSeriesImporter(Func<AnimeBatchDbContext> factory)
    {
        _factory = factory;
    }

    public static List<YmlEntry> Parse(string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<List<YmlEntry>>(yamlText) ?? [];
    }

    public async Task<YmlImportReport> ImportAsync(string yamlText)
    {
        var entries = Parse(yamlText);
        var skipped = new List<(string Name, string Reason)>();
        var imported = 0;
        var updated = 0;

        await using var db = _factory();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.Data is null)
            {
                skipped.Add((entry.Name, "sem bloco 'data'"));
                continue;
            }

            var normalized = ChapterService.CleanSeriesName(entry.Name);
            if (string.IsNullOrEmpty(normalized))
            {
                skipped.Add((entry.Name, "nome vazio"));
                continue;
            }

            if (!seen.Add(normalized))
            {
                skipped.Add((entry.Name, "duplicada no arquivo (a primeira vence)"));
                continue;
            }

            var existing = await db.Series.FirstOrDefaultAsync(s => s.NormalizedName == normalized).ConfigureAwait(false);
            if (existing is not null)
            {
                existing.Name = entry.Name;
                existing.EpisodeKbps = entry.Data.Min;
                existing.OpeningKbps = entry.Data.Max;
                existing.EndingKbps = entry.Data.End;
                updated++;
            }
            else
            {
                db.Series.Add(new Series
                {
                    Name = entry.Name,
                    NormalizedName = normalized,
                    EpisodeKbps = entry.Data.Min,
                    OpeningKbps = entry.Data.Max,
                    EndingKbps = entry.Data.End,
                });
                imported++;
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new YmlImportReport(imported, updated, skipped);
    }
}
