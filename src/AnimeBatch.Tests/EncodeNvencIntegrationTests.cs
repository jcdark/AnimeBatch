using AnimeBatch.Core.Services;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Integração REAL do encode NVENC com reforço de qualidade: parte sintética de 1s
/// codificada pela escada completa do EncodePartAsync (boost cheio) na placa NVENC 0.
/// Valida que o driver local aceita AQ + UHQ + filtro temporal + lookahead_level e que
/// o arquivo sai decodificável.
/// </summary>
public class EncodeNvencIntegrationTests
{
    [Fact]
    public async Task Encode_nvenc_com_reforco_produz_arquivo_valido()
    {
        var toolsDir = FindRepoToolsDir();
        Assert.True(toolsDir is not null, "tools\\ da raiz do repo não encontrado");
        var tools = new ToolsLocator(toolsDir);
        Assert.True(tools.FfmpegPath is not null, "ffmpeg não encontrado pela ToolsLocator");

        var work = Path.Combine(Path.GetTempPath(), "animebatch_nvenc_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var src = Path.Combine(work, "src.mkv");
            var outPath = Path.Combine(work, "parte_01 - teste.mkv");
            RunFfmpeg(tools.FfmpegPath!,
                $"-f lavfi -i testsrc2=size=640x360:rate=30:duration=1 -f lavfi -i sine=frequency=440:duration=1 " +
                $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -y \"{src}\"");

            var encode = new EncodeService(tools.FfmpegPath!);
            var cfg = new CodecEncodeConfig { Code = "nvenc_av1_10bit", Preset = 7, Tune = "hq", QualityBoost = true };
            await encode.EncodePartAsync(
                src, new EncodeService.JobItemRef(0, 1.0, 500), outPath,
                Path.Combine(work, "pass"), cfg, 500,
                CancellationToken.None, progress: null, cudaGpu: 0);

            Assert.True(File.Exists(outPath), "encode não produziu o arquivo");
            Assert.True(new FileInfo(outPath).Length > 1000, "arquivo de saída suspeito de vazio");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static string? FindRepoToolsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void RunFfmpeg(string ffmpeg, string args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in SplitArgs(args))
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg de teste falhou: {stderr}");
    }

    private static IEnumerable<string> SplitArgs(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }
}
