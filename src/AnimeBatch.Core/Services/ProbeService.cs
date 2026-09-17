using System.Diagnostics;
using System.Text.Json;

namespace AnimeBatch.Core.Services;

public record AudioStreamInfo(int Index, int Channels, string Language);
public record SubtitleStreamInfo(int Index, string Language);
public record ChapterInfo(int Number, string Title, double StartSeconds, double SourceEndSeconds);

public record EpisodeInfo(
    string Path,
    double DurationSeconds,
    IReadOnlyList<AudioStreamInfo> AudioStreams,
    IReadOnlyList<SubtitleStreamInfo> SubtitleStreams,
    IReadOnlyList<ChapterInfo> Chapters,
    int Width,
    int Height,
    double Fps);

/// <summary>Wrapper do ffprobe: duração, trilhas, legendas e capítulos de um episódio em uma chamada.</summary>
public class ProbeService : Queueing.IProbeStage
{
    /// <summary>Timeout rígido de cada probe: ffprobe responde em segundos num arquivo
    /// saudável; acima disso (arquivo numa rede morta, disco com falha) é travamento.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly string _ffprobePath;

    public ProbeService(string ffprobePath)
    {
        _ffprobePath = ffprobePath;
    }

    public async Task<EpisodeInfo> ProbeAsync(string videoPath, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error", "-of", "json",
            "-show_format", "-show_streams", "-show_chapters",
            videoPath,
        };

        var json = await RunCaptureOutputAsync(args, ct).ConfigureAwait(false);
        return Parse(json, videoPath);
    }

    public static EpisodeInfo Parse(string json, string videoPath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double duration = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durEl) &&
            double.TryParse(durEl.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            duration = d;
        }

        var audio = new List<AudioStreamInfo>();
        var subtitles = new List<SubtitleStreamInfo>();
        int width = 0, height = 0;
        var fps = 0.0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var index = s.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idx) ? idx : 0;
                var codecType = s.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() : null;
                var language = s.TryGetProperty("tags", out var tags) &&
                               tags.TryGetProperty("language", out var langEl) ? (langEl.GetString() ?? "") : "";

                if (codecType == "video" && width == 0)
                {
                    width = s.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var w) ? w : 0;
                    height = s.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var h) ? h : 0;
                    // avg_frame_rate primeiro ("24000/1001"); cai pro r_frame_rate se vier "0/0"
                    var fpsValue = s.TryGetProperty("avg_frame_rate", out var avgEl) ? avgEl.GetString() : null;
                    if (ParseFps(fpsValue) <= 0)
                        fpsValue = s.TryGetProperty("r_frame_rate", out var rEl) ? rEl.GetString() : null;
                    fps = ParseFps(fpsValue);
                }
                else if (codecType == "audio")
                {
                    var channels = s.TryGetProperty("channels", out var chEl) && chEl.TryGetInt32(out var ch) ? ch : 0;
                    audio.Add(new AudioStreamInfo(index, channels, language.ToLowerInvariant()));
                }
                else if (codecType == "subtitle")
                {
                    subtitles.Add(new SubtitleStreamInfo(index, language.ToLowerInvariant()));
                }
            }
        }

        var chapters = new List<ChapterInfo>();
        if (root.TryGetProperty("chapters", out var chaps) && chaps.ValueKind == JsonValueKind.Array)
        {
            var n = 1;
            foreach (var c in chaps.EnumerateArray())
            {
                var start = ParseSeconds(c, "start_time");
                var end = ParseSeconds(c, "end_time");
                var title = c.TryGetProperty("tags", out var tags) &&
                            tags.TryGetProperty("title", out var titleEl) ? (titleEl.GetString() ?? "") : "";
                chapters.Add(new ChapterInfo(n, title.Trim(), start, end));
                n++;
            }
        }

        return new EpisodeInfo(videoPath, duration, audio, subtitles, chapters, width, height, fps);
    }

    /// <summary>Converte "24000/1001" (avg/r_frame_rate do ffprobe) em fps; 0 se inválido.</summary>
    public static double ParseFps(string? ratio)
    {
        if (string.IsNullOrEmpty(ratio))
            return 0;
        var parts = ratio.Split('/');
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var num))
            return 0;
        var den = 1.0;
        if (parts.Length == 2 && (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out den) || den == 0))
            return 0;
        return den == 0 ? 0 : num / den;
    }

    private static double ParseSeconds(JsonElement element, string property) =>
        element.TryGetProperty(property, out var el) &&
        double.TryParse(el.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;

    private async Task<string> RunCaptureOutputAsync(string[] args, CancellationToken ct)
    {
        using var proc = ProcessRunner.Start(_ffprobePath, args,
            configure: psi => psi.StandardOutputEncoding = System.Text.Encoding.UTF8);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);
        try
        {
            var stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffprobe falhou (código {proc.ExitCode}): {stderr}");

            return stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // estourou o timeout (o token do chamador segue vivo) — mata o ffprobe travado
            ProcessRunner.TryKill(proc);
            throw new InvalidOperationException(
                $"ffprobe não respondeu em {ProbeTimeout.TotalSeconds:0}s — provável arquivo/disco travado.");
        }
    }
}
