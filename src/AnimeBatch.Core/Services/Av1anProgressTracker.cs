using System.Globalization;
using System.Text.RegularExpressions;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Progresso do motor Av1an reconstruído das linhas do logfile (-l). O av1an só escreve
/// "started chunk"/"finished chunk" UMA VEZ POR CHUNK — as duas passadas do 2-pass rodam
/// DENTRO do chunk sem linha alguma, e o scenecut/segmentação também é silencioso por
/// minutos —, então fps e velocidade são médias desde o primeiro chunk iniciado:
/// fps = frames concluídos / segundo de relógio; speed = fração de chunks concluídos ×
/// duração da parte / segundo de relógio. Formato real das linhas do logfile:
/// <code>
/// Queue 165 Workers 16 Encoder svt-av1 Passes 2
/// ... started chunk 00000: 180 frames
/// ... finished chunk 00001: 151 frames, 53.10 fps, took 2.84s
/// </code>
/// As linhas chegam por dois caminhos concorrentes (stderr e tail do logfile) — a classe é thread-safe.
/// </summary>
public sealed class Av1anProgressTracker
{
    private static readonly Regex QueueRegex = new(@"Queue (\d+) Workers", RegexOptions.Compiled);
    private static readonly Regex StartedRegex = new(@"started chunk \d+: (\d+) frames", RegexOptions.Compiled);
    private static readonly Regex FinishedRegex = new(@"finished chunk \d+: (\d+) frames", RegexOptions.Compiled);

    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private long _totalChunks;
    private long _finishedChunks;
    private long _finishedFrames;
    private long? _firstStartTick;

    /// <summary>Fase inicial = análise de cenas: é a PRIMEIRA coisa que o av1an faz e é
    /// silenciosa até o resumo "scenecut: found" (a CPU fica em ~1 núcleo nesse período).</summary>
    private string _phase = "scenes";

    public Av1anProgressTracker(Func<long>? clock = null) =>
        _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>Consome uma linha do logfile. Devolve true quando foi um "finished chunk"
    /// (vale publicar o instantâneo na hora; fora isso o chamador publica por tick de 1s).</summary>
    public bool FeedLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        var q = QueueRegex.Match(line);
        if (q.Success &&
            long.TryParse(q.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tq))
        {
            lock (_gate) { _totalChunks = tq; }
            return false;
        }

        // fase do pipeline do av1an (a segmentação pode levar minutos — o rodapé precisa
        // dizer EM QUE está em vez de só mostrar 0.0). Códigos crus; a UI traduz.
        // "scenecut: found" é o resumo FINAL da análise — depois dela vem a montagem dos
        // chunks (silenciosa), que não é análise: sem isso o rodapé ficava "analisando
        // cenas" durante toda a preparação.
        if (line.Contains("scenecut: found", StringComparison.Ordinal))
        {
            lock (_gate) { _phase = "preparing"; }
        }
        else if (line.Contains("scenecut", StringComparison.Ordinal))
        {
            lock (_gate) { _phase = "scenes"; }
        }
        else if (line.Contains("Segmenting video", StringComparison.Ordinal))
        {
            lock (_gate) { _phase = "segmenting"; }
        }

        if (StartedRegex.IsMatch(line))
        {
            lock (_gate)
            {
                _firstStartTick ??= _clock();
                _phase = "chunks";
            }
            return false;
        }

        var f = FinishedRegex.Match(line);
        if (f.Success &&
            long.TryParse(f.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
        {
            lock (_gate)
            {
                _finishedFrames += frames;
                _finishedChunks++;
                _phase = "chunks";
            }
            return true;
        }

        return false;
    }

    /// <summary>Relato de batimento para os ticks sem chunk concluído ainda: zeros, mas com
    /// a fase corrente e o total de chunks já conhecido (o rodapé mostra vida e contexto).</summary>
    public EncodeProgress Heartbeat()
    {
        lock (_gate)
        {
            return new EncodeProgress(0, 0, 0,
                ChunksDone: 0, ChunksTotal: (int)Math.Min(_totalChunks, int.MaxValue), Phase: _phase);
        }
    }

    /// <summary>Instantâneo do andamento (null = nada mensurável ainda: nenhum chunk
    /// concluído — o relato começa no primeiro "finished chunk", quando fps faz sentido;
    /// nesses ticks o chamador usa o Heartbeat).</summary>
    public EncodeProgress? Snapshot(double durationSeconds)
    {
        if (durationSeconds <= 0)
            return null;
        long total, done, frames;
        long? start;
        lock (_gate)
        {
            (total, done, frames, start) = (_totalChunks, _finishedChunks, _finishedFrames, _firstStartTick);
        }
        if (start is not { } firstStart || done == 0)
            return null;

        var elapsed = Math.Max((_clock() - firstStart) / 1000.0, 0.001);
        var fps = frames / elapsed;
        double speed = 0, outSeconds = 0;
        if (total > 0)
        {
            outSeconds = Math.Clamp(done / (double)total, 0, 1) * durationSeconds;
            speed = outSeconds / elapsed;
        }
        return new EncodeProgress(fps, speed, outSeconds,
            ChunksDone: (int)Math.Min(done, int.MaxValue),
            ChunksTotal: (int)Math.Min(total, int.MaxValue),
            Phase: "chunks");
    }
}
