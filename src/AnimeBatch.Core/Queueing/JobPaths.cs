using AnimeBatch.Core.Services;

namespace AnimeBatch.Core.Queueing;

/// <summary>
/// Convenções de caminhos das partes de um job — fonte ÚNICA compartilhada pelo QueueRunner
/// (que escreve os arquivos) e pela tela de Episódios (que precisa saber quais partes já
/// existem no disco para reaproveitá-las na junção sem re-encodar). Extraído para os dois
/// lados nunca divergirem na construção do caminho.
/// </summary>
public static class JobPaths
{
    /// <summary>Pasta de trabalho das partes: {outputRoot}\AnimeBatch\{base}.</summary>
    public static string WorkDirectory(string outputRoot, string baseName) =>
        Path.Combine(outputRoot, "AnimeBatch", SafeFolder(baseName));

    /// <summary>Nome do arquivo da parte: "02 - Intro - {base}.mkv" (número = capítulo na grade).</summary>
    public static string PartFileName(int order, string title, string baseName) =>
        $"{order:00} - {ChapterService.SanitizeTitle(title)} - {baseName}.mkv";

    /// <summary>Caminho completo da parte de um capítulo do episódio {baseName}.</summary>
    public static string PartPath(string outputRoot, string baseName, int order, string title) =>
        Path.Combine(WorkDirectory(outputRoot, baseName), PartFileName(order, title, baseName));

    public static string SafeFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "episodio" : s;
    }
}
