using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnimeBatch.Core.Services;

/// <summary>Capítulo do snapshot da grade REGULAR, gravado no job quando ele é enfileirado com
/// Calibragem Automática (Job.FinalChaptersJson): os cortes/parts vêm dos blocos de calibragem,
/// mas os marcadores de capítulo do arquivo final continuam saindo desta grade.</summary>
public record FinalChapter(int Number, string Title, double StartSeconds);

/// <summary>
/// Concatena as partes com mkvmerge na ordem informada e gera o arquivo de capítulos
/// cumulativos (formato OGM simples). Capítulos da classe Critical participam do vídeo
/// mas NÃO viram capítulos no arquivo final.
/// </summary>
public class MergeService : Queueing.IMergeStage
{
    private readonly string _mkvmerge;

    public MergeService(string mkvmergePath)
    {
        _mkvmerge = mkvmergePath;
    }

    /// <summary>Monta o conteúdo do arquivo de capítulos (uma entrada por parte não-crítica).</summary>
    public static string BuildChaptersTxt(IEnumerable<(string Title, double StartSeconds, double EndSeconds, bool IsCritical)> parts)
    {
        var sb = new StringBuilder();
        var cumulative = 0.0;
        var number = 1;

        foreach (var (title, start, end, isCritical) in parts)
        {
            if (!isCritical)
            {
                var ts = TimeSpan.FromSeconds(cumulative);
                sb.AppendLine($"CHAPTER{number:00}={ts:hh\\:mm\\:ss\\.fff}");
                sb.AppendLine($"CHAPTER{number:00}NAME={title}");
                number++;
            }
            cumulative += Math.Max(0, end - start);
        }

        return sb.ToString();
    }

    /// <summary>Serializa o snapshot de capítulos finais gravado no Job (formato compacto e
    /// estável — número, título e início na linha do tempo da ORIGEM, que é a mesma do final
    /// porque os blocos de calibragem cobrem o vídeo inteiro em ordem).</summary>
    public static string BuildFinalChaptersJson(IEnumerable<FinalChapter> chapters) =>
        JsonSerializer.Serialize(chapters.Select(c => new { n = c.Number, t = c.Title, s = c.StartSeconds }));

    /// <summary>Lê o snapshot gravado no Job; null/inválido → null (o merge cai no
    /// comportamento padrão, que com calibragem resulta em arquivo sem entradas de capítulo).</summary>
    public static List<FinalChapter>? ParseFinalChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<FinalChapter>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                list.Add(new FinalChapter(
                    c.TryGetProperty("n", out var n) && n.TryGetInt32(out var nv) ? nv : 0,
                    c.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "",
                    c.TryGetProperty("s", out var s) && s.TryGetDouble(out var sv) ? sv : 0));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Capítulos a partir do snapshot: os tempos JÁ são absolutos na linha do tempo do
    /// vídeo (não cumulativos como em BuildChaptersTxt).</summary>
    public static string BuildFinalChaptersTxt(IEnumerable<FinalChapter> chapters)
    {
        var sb = new StringBuilder();
        var number = 1;
        foreach (var c in chapters.OrderBy(c => c.StartSeconds))
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, c.StartSeconds));
            sb.AppendLine($"CHAPTER{number:00}={ts:hh\\:mm\\:ss\\.fff}");
            sb.AppendLine($"CHAPTER{number:00}NAME={c.Title}");
            number++;
        }
        return sb.ToString();
    }

    public async Task MergeAsync(
        IReadOnlyList<(string PartPath, string Title, double StartSeconds, double EndSeconds, bool IsCritical)> parts,
        string finalPath,
        string chaptersPath,
        CancellationToken ct,
        string? chaptersTxtOverride = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // Override = snapshot dos capítulos regulares (jobs com Calibragem Automática: TODAS as
        // partes são temporárias e o BuildChaptersTxt das partes viria vazio).
        var chaptersTxt = chaptersTxtOverride ?? BuildChaptersTxt(parts.Select(p => (p.Title, p.StartSeconds, p.EndSeconds, p.IsCritical)));
        if (chaptersTxt.Trim().Length > 0)
            await File.WriteAllTextAsync(chaptersPath, chaptersTxt, ct).ConfigureAwait(false);

        var args = new List<string> { "-o", finalPath };
        if (chaptersTxt.Trim().Length > 0)
            args.AddRange(["--chapters", chaptersPath]);

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                args.Add("+"); // append ao anterior
            // Partes codificadas pelo HandBrake CARREGAM os capítulos da origem — sem o
            // --no-chapters o mkvmerge prefere os capítulos das entradas e descarta o
            // nosso chapters.txt (o final saía com os capítulos do arquivo original).
            args.Add("--no-chapters");
            args.Add(parts[i].PartPath);
        }

        using var proc = ProcessRunner.Start(_mkvmerge, args);
        var outputTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(proc);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        // mkvmerge: 0 = ok, 1 = avisos (arquivo gerado), >= 2 = erro
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"mkvmerge falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(error + " " + output, 800)}");
    }
}
