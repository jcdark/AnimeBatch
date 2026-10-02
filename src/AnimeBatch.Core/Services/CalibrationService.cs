using System.Globalization;
using System.Text.Json;
using AnimeBatch.Core.Models;

namespace AnimeBatch.Core.Services;

/// <summary>Uma janela de análise do vídeo: trecho fixo com o bitrate (kbps) que a encode de
/// análise realmente gastou nele — a matéria-prima da classificação por criticidade.</summary>
public record CalibrationWindow(double Start, double End, double Kbps);

/// <summary>Um bloco final da grade de calibragem: janelas adjacentes do mesmo nível mescladas.
/// Number é 1-based e contíguo (a grade de calibragem é renumerada do zero, sem buracos).</summary>
public record CalibrationBlock(int Number, CalibrationLevel Level, double StartSeconds, double EndSeconds);

/// <summary>Limites das 5 faixas derivados das janelas: Min/Max medidos e os 4 limites
/// superiores (VeryLow, Low, Normal, High) distribuídos em 20% do intervalo [Min..Max] cada.
/// A média de mais alto e mais baixo (o centro do intervalo) é o centro da faixa Normal.</summary>
public readonly record struct CalibrationThresholds(double Min, double Max, double[] UpperBounds);

/// <summary>Relato de andamento da análise para o modal (Percent 0–100 na fase de encode).</summary>
public readonly record struct CalibrationProgress(double Percent, string Phase);

/// <summary>Resultado completo da análise: blocos mesclados + o intervalo medido (min/máx em
/// kbps, para o modal mostrar contexto) + o bitrate de análise usado.</summary>
public record CalibrationResult(
    IReadOnlyList<CalibrationBlock> Blocks,
    double MinKbps,
    double MaxKbps,
    int AnalysisKbps);

/// <summary>
/// Calibragem Automática: roda uma encode de ANÁLISE do vídeo inteiro (SVT 1-pass @1000 kbps,
/// preset rápido) e mede, por janela de alguns segundos, quantos bits o encoder gastou de fato
/// — cenas complexas consomem acima da média; estáticas, abaixo. Das janelas saem os limites
/// entre Muito Baixo/Baixo/Normal/Alto/Muito Alto (o intervalo [mín..máx] dividido em 5 faixas
/// iguais) e os blocos da grade de calibragem (janelas adjacentes do mesmo nível mescladas).
/// A calibragem NÃO encodea produção: só mede e propõe a grade.
/// </summary>
public class CalibrationService(string ffmpegPath, string ffprobePath)
{
    /// <summary>Bitrate da encode de análise (decisão do dono: 1000 kbps fixos).</summary>
    public const int AnalysisKbps = 1000;

    /// <summary>Preset do SVT na encode de análise — rápido de propósito: a distribuição de
    /// bits por cena muda pouco de preset para preset, e o tempo de calibragem cai uma ordem
    /// de grandeza em relação ao preset de produção.</summary>
    public const int AnalysisPreset = 11;

    /// <summary>Tamanho da janela de análise em segundos (blocos menores viram ruído; maiores
    /// perdem cenas curtas).</summary>
    public const double WindowSeconds = 5.0;

    private readonly string _ffmpeg = ffmpegPath;
    private readonly string _ffprobe = ffprobePath;

    // ---- pipeline puro (testável sem processo) ----

    /// <summary>Limites das 5 faixas a partir dos kbps medidos por janela. Com tudo igual
    /// (mín==máx) a classificação degrada para Normal.</summary>
    public static CalibrationThresholds DeriveThresholds(IReadOnlyList<double> windowKbps)
    {
        if (windowKbps.Count == 0)
            return new CalibrationThresholds(0, 0, [0, 0, 0, 0]);
        var min = windowKbps.Min();
        var max = windowKbps.Max();
        var bounds = new double[4];
        if (max > min)
        {
            for (var i = 0; i < bounds.Length; i++)
                bounds[i] = min + (max - min) * (i + 1) / 5.0;
        }
        return new CalibrationThresholds(min, max, bounds);
    }

    /// <summary>Nível de uma janela dado os limites. Fora do intervalo medido (janela
    /// atravessando o fim do vídeo, arredondamento) gruda no extremo correspondente.</summary>
    public static CalibrationLevel Classify(CalibrationThresholds thresholds, double kbps)
    {
        if (thresholds.Max <= thresholds.Min)
            return CalibrationLevel.Normal; // vídeo homogêneo: tudo no nível do meio
        var b = thresholds.UpperBounds;
        if (kbps < b[0]) return CalibrationLevel.VeryLow;
        if (kbps < b[1]) return CalibrationLevel.Low;
        if (kbps < b[2]) return CalibrationLevel.Normal;
        if (kbps < b[3]) return CalibrationLevel.High;
        return CalibrationLevel.VeryHigh;
    }

    /// <summary>Janelas fixas de <see cref="WindowSeconds"/> alinhadas do zero; a última corta
    /// na duração do vídeo (e o kbps dela é calculado sobre a duração real da janela).</summary>
    public static List<CalibrationWindow> BuildWindows(double durationSeconds, IReadOnlyList<double> windowKbps)
    {
        if (durationSeconds <= 0)
            return [];
        var count = Math.Max(1, (int)Math.Ceiling(durationSeconds / WindowSeconds));
        var windows = new List<CalibrationWindow>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * WindowSeconds;
            var end = Math.Min(start + WindowSeconds, durationSeconds);
            var kbps = i < windowKbps.Count ? windowKbps[i] : 0;
            windows.Add(new CalibrationWindow(start, end, kbps));
        }
        return windows;
    }

    /// <summary>Mescla janelas adjacentes do mesmo nível em blocos renumerados 1..N.</summary>
    public static List<CalibrationBlock> BuildBlocks(IReadOnlyList<CalibrationWindow> windows)
    {
        var thresholds = DeriveThresholds([.. windows.Select(w => w.Kbps)]);
        var blocks = new List<CalibrationBlock>();
        foreach (var w in windows)
        {
            var level = Classify(thresholds, w.Kbps);
            if (blocks.Count > 0 && blocks[^1].Level == level)
            {
                var last = blocks[^1];
                blocks[^1] = last with { EndSeconds = w.End };
            }
            else
            {
                blocks.Add(new CalibrationBlock(blocks.Count + 1, level, w.Start, w.End));
            }
        }
        return blocks;
    }

    // ---- análise com processo ----

    /// <summary>Analisa o vídeo inteiro: encode de análise (SVT 1-pass) → soma de bytes por
    /// janela via ffprobe → níveis → blocos. O arquivo do probe é temporário e sempre apagado.</summary>
    public async Task<CalibrationResult> AnalyzeAsync(
        string sourcePath, double durationSeconds,
        IProgress<CalibrationProgress>? progress = null, CancellationToken ct = default)
    {
        if (durationSeconds <= 0)
            throw new InvalidOperationException("Duração do vídeo desconhecida — probe antes de calibrar.");

        var probeFile = Path.Combine(Path.GetTempPath(), $"animebatch-calib-{Guid.NewGuid():N}.mkv");
        try
        {
            progress?.Report(new CalibrationProgress(0, "encode"));
            await RunAnalysisEncodeAsync(sourcePath, probeFile, durationSeconds, progress, ct).ConfigureAwait(false);

            progress?.Report(new CalibrationProgress(100, "measure"));
            var windowKbps = await MeasureWindowKbpsAsync(probeFile, durationSeconds, ct).ConfigureAwait(false);
            if (windowKbps.Count == 0)
                throw new InvalidOperationException("A análise não produziu dados de bitrate (arquivo sem vídeo?).");

            var windows = BuildWindows(durationSeconds, windowKbps);
            var thresholds = DeriveThresholds([.. windows.Select(w => w.Kbps)]);
            var blocks = BuildBlocks(windows);
            return new CalibrationResult(blocks, thresholds.Min, thresholds.Max, AnalysisKbps);
        }
        finally
        {
            TryDelete(probeFile);
        }
    }

    /// <summary>Encode de análise: só vídeo (sem áudio/legendas), SVT 1-pass — o bitrate alvo é
    /// constante e a DISTRIBUIÇÃO entre cenas é o que interessa. Progresso por -progress pipe:1.</summary>
    private async Task RunAnalysisEncodeAsync(
        string sourcePath, string probeFile, double durationSeconds,
        IProgress<CalibrationProgress>? progress, CancellationToken ct)
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", sourcePath,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-c:v", "libsvtav1",
            "-preset", AnalysisPreset.ToString(CultureInfo.InvariantCulture),
            "-b:v", $"{AnalysisKbps.ToString(CultureInfo.InvariantCulture)}k",
            "-pix_fmt", "yuv420p",
            "-f", "matroska", probeFile,
            "-progress", "pipe:1", "-nostats",
        };

        using var proc = ProcessRunner.Start(_ffmpeg, args);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        async Task PumpProgressAsync()
        {
            while (await proc.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (EncodeService.TryParseOutTimeSeconds(line, out var seconds) && durationSeconds > 0)
                    progress?.Report(new CalibrationProgress(
                        Math.Clamp(seconds / durationSeconds * 100.0, 0, 100), "encode"));
            }
        }

        var pumpTask = PumpProgressAsync();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(proc);
            throw;
        }
        await pumpTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"A encode de análise falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(await stderrTask.ConfigureAwait(false), 400)}");
    }

    /// <summary>Soma os bytes dos pacotes de vídeo do probe por janela de tempo e converte em
    /// kbps médios de cada janela (bits × 8 ÷ duração da janela ÷ 1000).</summary>
    public async Task<List<double>> MeasureWindowKbpsAsync(string probeFile, double durationSeconds, CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,size",
            "-of", "json", probeFile,
        };
        // Capture é síncrono e bloqueante — roda fora da thread da UI; ct não derruba o
        // ffprobe no meio (timeout rígido de 300s cobre o pior caso).
        var capture = await Task.Run(() => ProcessRunner.Capture(_ffprobe, args, TimeoutMs), ct).ConfigureAwait(false);
        if (!capture.Ok || string.IsNullOrEmpty(capture.Stdout))
            throw new InvalidOperationException("ffprobe não conseguiu ler os pacotes do arquivo de análise.");
        return ParseWindowKbps(capture.Stdout, durationSeconds);
    }

    /// <summary>Parse puro do JSON de packets do ffprobe → kbps por janela (testável).
    /// Array vazio/sem packets → lista vazia (a análise não mediu nada) — o chamador
    /// transforma isso em erro amigável em vez de uma grade "Normal" sem sentido.</summary>
    public static List<double> ParseWindowKbps(string json, double durationSeconds)
    {
        var count = Math.Max(1, (int)Math.Ceiling(durationSeconds / WindowSeconds));
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
            return [];
        if (packets.GetArrayLength() == 0)
            return [];

        var bytes = new long[count];
        foreach (var p in packets.EnumerateArray())
        {
            var pts = p.TryGetProperty("pts_time", out var ptEl) &&
                      double.TryParse(ptEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pt)
                ? pt
                : 0;
            var size = ParsePacketSize(p);
            var idx = (int)(pts / WindowSeconds);
            if (idx < 0) idx = 0;
            if (idx >= count) idx = count - 1;
            bytes[idx] += size;
        }

        var result = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * WindowSeconds;
            var windowDuration = Math.Min(start + WindowSeconds, durationSeconds) - start;
            result.Add(windowDuration > 0 ? bytes[i] * 8.0 / windowDuration / 1000.0 : 0);
        }
        return result;
    }

    /// <summary>O ffprobe devolve "size" como string no -of json; tolera número, string e
    /// ausência (0) — TryGetInt64 LANÇA em ValueKind String, então checa o tipo antes.</summary>
    private static long ParsePacketSize(JsonElement packet)
    {
        if (!packet.TryGetProperty("size", out var el))
            return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var v) ? v : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }

    private static void TryDelete(string path)
    {
        // o ffmpeg recém-morto pode demorar um instante a soltar o handle do probe —
        // algumas retentativas curtas cobrem isso sem travar a UI
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 7) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (attempt < 7) { Thread.Sleep(150); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>ffprobe de packets pode levar mais que um probe comum num arquivo grande
    /// (lê o probe inteiro), mas o probe é @1000kbps — minutos bastam.</summary>
    private static readonly int TimeoutMs = 300_000;
}
