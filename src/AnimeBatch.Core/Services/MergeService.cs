using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AnimeBatch.Core.Services;

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

    public async Task MergeAsync(
        IReadOnlyList<(string PartPath, string Title, double StartSeconds, double EndSeconds, bool IsCritical)> parts,
        string finalPath,
        string chaptersPath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var chaptersTxt = BuildChaptersTxt(parts.Select(p => (p.Title, p.StartSeconds, p.EndSeconds, p.IsCritical)));
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
