using AnimeBatch.Core.Models;

namespace AnimeBatch.Core.Services;

public record ChapterRange(int Number, string Title, double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>
/// Lógica de capítulos do converter.py, portada: intervalos por capítulo (com regra de capítulo
/// muito curto), classificação OP/ED por palavras-chave e limpeza do nome da série para o match.
/// </summary>
public class ChapterService
{
    /// <summary>Capítulos com duração menor que isso são descartados (evita encoder com 0 frames).</summary>
    public double MinSegmentDurationSeconds { get; init; } = 0.5;

    /// <summary>
    /// Constrói os intervalos encodáveis a partir dos capítulos sondados, replicando o script:
    /// o fim de cada capítulo é o início do próximo menos um epsilon; o último vai até a duração
    /// total do arquivo. Capítulos curtos demais são descartados.
    /// </summary>
    public List<ChapterRange> BuildRanges(IReadOnlyList<ChapterInfo> chapters, double duration)
    {
        const double epsilon = 0.001;
        var ranges = new List<ChapterRange>();

        for (var i = 0; i < chapters.Count; i++)
        {
            var c = chapters[i];
            var start = c.StartSeconds;
            var end = i + 1 < chapters.Count
                ? Math.Max(start + epsilon, chapters[i + 1].StartSeconds - epsilon)
                : Math.Max(start + epsilon, duration);

            if (end - start < MinSegmentDurationSeconds)
                continue;

            var title = string.IsNullOrWhiteSpace(c.Title) ? $"Capitulo {c.Number}" : c.Title;
            ranges.Add(new ChapterRange(c.Number, title, start, end));
        }

        // Arquivo sem capítulos (ou sem trecho útil): um capítulo único cobrindo o vídeo
        // inteiro — senão a fila ficaria sem partes para encodar
        if (ranges.Count == 0 && duration > 0)
            ranges.Add(new ChapterRange(1, "Capitulo 1", 0, duration));

        return ranges;
    }

    /// <summary>Palavras que marcam cena crítica (usa bitrate alto; não vira capítulo no arquivo final).</summary>
    public static readonly string[] CriticalWords = ["crítico", "critico", "critical"];

    /// <summary>
    /// Classifica um capítulo por palavras-chave no título (substring, case-insensitive).
    /// Cena crítica tem precedência máxima; depois abertura, depois encerramento.
    /// </summary>
    public static BitrateClass Classify(string title, IEnumerable<string> opWords, IEnumerable<string> endWords)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        if (CriticalWords.Any(w => t.Contains(w)))
            return BitrateClass.Critical;
        if (opWords.Any(w => t.Contains(w.ToLowerInvariant())))
            return BitrateClass.Opening;
        if (endWords.Any(w => t.Contains(w.ToLowerInvariant())))
            return BitrateClass.Ending;
        return BitrateClass.Episode;
    }

    public static BitrateClass Classify(string title, IReadOnlyList<KeywordRule> keywords) =>
        Classify(
            title,
            keywords.Where(k => k.Category == KeywordCategory.Op).Select(k => k.Word),
            keywords.Where(k => k.Category == KeywordCategory.End).Select(k => k.Word));

    /// <summary>
    /// Nome de série normalizado para o match com o banco — mesma regra do script:
    /// remove extensão, remove o sufixo " - SXXEXX..." e converte para minúsculas.
    /// </summary>
    public static string CleanSeriesName(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s-\s[sS]\d+[eE]\d+.*$", "");
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Nome de série pra EXIBIÇÃO, mantendo a capitalização original do arquivo
    /// (ex.: "BLACK TORCH - S01E10.mkv" → "BLACK TORCH").
    /// </summary>
    public static string StripEpisodeSuffix(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s-\s[sS]\d+[eE]\d+.*$", "");
        return name.Trim();
    }

    /// <summary>Palavras-chave padrão, usadas para semear o banco na primeira execução.</summary>
    public static IReadOnlyList<KeywordRule> DefaultKeywords() =>
    [
        new KeywordRule { Category = KeywordCategory.Op, Word = "op" },
        new KeywordRule { Category = KeywordCategory.Op, Word = "opening" },
        new KeywordRule { Category = KeywordCategory.Op, Word = "opened" },
        new KeywordRule { Category = KeywordCategory.Op, Word = "abertura" },
        new KeywordRule { Category = KeywordCategory.Op, Word = "open" },
        new KeywordRule { Category = KeywordCategory.Op, Word = "intro" },
        new KeywordRule { Category = KeywordCategory.End, Word = "ed" },
        new KeywordRule { Category = KeywordCategory.End, Word = "end" },
        new KeywordRule { Category = KeywordCategory.End, Word = "ending" },
        new KeywordRule { Category = KeywordCategory.End, Word = "finalização" },
        new KeywordRule { Category = KeywordCategory.End, Word = "finalizacao" },
        new KeywordRule { Category = KeywordCategory.End, Word = "credits" },
    ];

    /// <summary>Nome de arquivo de saída de uma parte, no formato do script: "N - Título - Base.mkv".</summary>
    public static string BuildPartFileName(int displayNumber, string title, string sourceBaseName)
    {
        var safe = SanitizeTitle(title);
        return $"{displayNumber} - {safe} - {sourceBaseName}.mkv";
    }

    /// <summary>Remove caracteres inválidos para nome de arquivo e limita o título a 60 caracteres.</summary>
    public static string SanitizeTitle(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(title.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (s.Length > 60)
            s = s[..60].TrimEnd();
        return string.IsNullOrEmpty(s) ? "chapter" : s;
    }
}
