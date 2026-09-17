using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AnimeBatch.Core.Services;

/// <summary>Instantâneo do andamento do encode, lido do -progress do ffmpeg.</summary>
public record EncodeProgress(double Fps, double Speed, double OutTimeSeconds);

/// <summary>Nível do reforço de qualidade do NVENC (só tem efeito nos codecs nvenc_*).</summary>
public enum NvencBoost
{
    /// <summary>Argumentos clássicos (preset/tune/VBV/multipass/lookahead).</summary>
    Off,
    /// <summary>Só AQ espacial+temporal — aceito por toda placa NVENC.</summary>
    AqOnly,
    /// <summary>AQ + tune UHQ + filtro temporal + lookahead_level (análise estendida).</summary>
    Full,
}

/// <summary>
/// Codifica uma parte (trecho do episódio) com ffmpeg, nos 4 codecs suportados:
/// svt_av1 / svt_av1_10bit (CPU, VBR single-pass) e nvenc_av1 / nvenc_av1_10bit (GPU).
/// Áudio da primeira trilha vai em AAC 160k; legendas ficam pro remux (M3).
/// </summary>
public class EncodeService
{
    /// <summary>Tempo sem NENHUMA linha de saída do ffmpeg antes de matar o processo por
    /// stall (evita worker preso a noite inteira num encode travado). Configurável via
    /// setting "queue.stallMinutes"; o default cobre encodes lentos sem falsos positivos.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(10);

    private const int StallPollIntervalMs = 15_000;

    private readonly string _ffmpeg;
    private readonly TimeSpan _stallTimeout;

    public EncodeService(string ffmpegPath, TimeSpan? stallTimeout = null)
    {
        _ffmpeg = ffmpegPath;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
    }

    /// <summary>
    /// Monta os argumentos do encoder a partir da configuração da aba Encodes.
    /// NVENC: preset p1–p7, tune, CQ ou VBR com teto, multipass, lookahead, perfil/nível,
    /// reforço de qualidade (NvencBoost) e -gpu para dirigir o encode a uma placa.
    /// SVT: preset 1–13, tune (svtav1-params), CRF ou VBR, 2-pass quando Multipass+bitrate,
    /// perfil/nível (numéricos). SVT não suporta VBV (maxrate/bufsize).
    /// Devolve args DISCRETOS (um token por item) — nunca separe uma string por espaço:
    /// qualquer valor que ganhe um espaço um dia quebraria silenciosamente o comando.
    /// </summary>
    public static (IReadOnlyList<string> Args, bool TwoPass) BuildVideoArgs(
        CodecEncodeConfig cfg, int kbps, int? cudaGpu = null, NvencBoost boost = NvencBoost.Off)
    {
        var isNvenc = cfg.Code.StartsWith("nvenc", StringComparison.Ordinal);
        var is10Bit = cfg.Code.EndsWith("10bit", StringComparison.Ordinal);
        var pixFmt = is10Bit ? (isNvenc ? "p010le" : "yuv420p10le") : (isNvenc ? "yuv420p" : "yuv420p");

        // Reforço de qualidade: FastConversion vence (o objetivo dele é velocidade).
        // AqOnly roda em qualquer NVENC; Full acrescenta a análise estendida (UHQ/filtro
        // temporal/lookahead_level) — se o driver recusar, a escada de EncodePartAsync cai
        // para AqOnly e depois para o Off.
        var effectiveBoost = isNvenc && !cfg.FastConversion ? boost : NvencBoost.Off;
        // No reforço cheio, UHQ substitui hq/none; tune escolhido pelo usuário (ll/ull/lossless) é respeitado
        var tuneOverridden = effectiveBoost == NvencBoost.Full && cfg.Tune is "hq" or "none";

        var args = new List<string> { "-c:v", isNvenc ? "av1_nvenc" : "libsvtav1" };

        if (isNvenc)
            args.AddRange(["-preset", $"p{Math.Clamp(cfg.Preset, 1, 7)}"]);
        else
            args.AddRange(["-preset", $"{Math.Clamp(cfg.Preset, 1, 13)}"]);

        if (cfg.Tune != "none" && !tuneOverridden)
        {
            if (isNvenc)
                args.AddRange(["-tune", cfg.Tune]);
            else
                args.AddRange(["-svtav1-params", $"tune={cfg.Tune}"]);
        }

        if (cfg.UseConstantQuality)
        {
            if (isNvenc)
                args.AddRange(["-rc", "vbr", "-cq", $"{cfg.Cq}", "-b:v", "0"]);
            else
                args.AddRange(["-crf", $"{cfg.Cq}"]);
        }
        else if (isNvenc)
        {
            // teto de picos ~2,4x a média, tanque de 2 janelas de pico (validado na 5060 Ti)
            var maxrate = (int)(kbps * 2.4);
            args.AddRange(["-rc", "vbr", "-b:v", $"{kbps}k",
                "-maxrate", $"{maxrate}k", "-bufsize", $"{maxrate * 2}k"]);
        }
        else
        {
            args.AddRange(["-b:v", $"{kbps}k"]); // SVT não suporta VBV — só a média
        }

        if (isNvenc && !cfg.UseConstantQuality)
        {
            args.AddRange(cfg.Multipass ? ["-multipass", "fullres"] : ["-multipass", "disabled"]);
        } // no modo CQ o multipass fica oculto → usa o padrão do encoder

        if (isNvenc)
            args.AddRange(["-rc-lookahead", cfg.FastConversion ? "0" : "32"]);

        if (cfg.Profile != "none")
            args.AddRange(["-profile:v", cfg.Profile]);

        // comparação case-insensitive: o banco pode ter "Auto" (rótulo do combo) em vez de "auto"
        if (!string.Equals(cfg.Level, "auto", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-level", cfg.Level]);

        // "-gpu" segue o espaço de índices do NVENC (= ordem do nvidia-smi/CUDA)
        switch (effectiveBoost)
        {
            case NvencBoost.AqOnly:
                args.AddRange(["-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "8"]);
                break;
            case NvencBoost.Full:
                args.AddRange(["-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "8"]);
                if (tuneOverridden)
                    args.AddRange(["-tune", "uhq"]);
                args.AddRange(["-tf_level", "4", "-lookahead_level", "auto"]);
                break;
        }

        if (isNvenc && cudaGpu is { } gpu)
            args.AddRange(["-gpu", $"{gpu}"]);

        args.AddRange(["-pix_fmt", pixFmt]);

        // 2-pass de verdade só no SVT (bitrate médio + multipass); NVENC usa multipass interno
        var twoPass = !isNvenc && !cfg.UseConstantQuality && cfg.Multipass;
        return (args, twoPass);
    }

    /// <summary>
    /// Escada de tentativa do encode NVENC: reforço cheio → só AQ → base. Se o ffmpeg
    /// recusar um recurso (driver/GPU mais antiga), a variante seguinte ainda entrega o
    /// encode. Fora do NVENC, em Conversão Rápida ou com o reforço desligado: só a base.
    /// </summary>
    public static IReadOnlyList<(CodecEncodeConfig Cfg, NvencBoost Boost)> BoostVariants(CodecEncodeConfig cfg)
    {
        if (!cfg.Code.StartsWith("nvenc", StringComparison.Ordinal) || cfg.FastConversion || !cfg.QualityBoost)
            return [(cfg, NvencBoost.Off)];
        return [(cfg, NvencBoost.Full), (cfg, NvencBoost.AqOnly), (cfg, NvencBoost.Off)];
    }

    /// <summary>Codifica o trecho do item para outputPath. progress recebe fps/velocidade/tempo convertido.
    /// cudaGpu dirige o encode a uma placa (índice NVENC/nvidia-smi); nos codecs NVENC com reforço
    /// ligado, uma recusa do ffmpeg cai automaticamente para a variante seguinte da escada.</summary>
    public async Task EncodePartAsync(
        string sourcePath,
        JobItemRef item,
        string outputPath,
        string passLogBase,
        CodecEncodeConfig cfg,
        int kbps,
        CancellationToken ct,
        IProgress<EncodeProgress>? progress,
        int? cudaGpu = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var duration = Math.Max(0.001, item.EndSeconds - item.StartSeconds);

        var variants = BoostVariants(cfg);
        for (var i = 0; i < variants.Count; i++)
        {
            try
            {
                await EncodeWithVariantAsync(sourcePath, item, outputPath, passLogBase, duration,
                    variants[i].Cfg, kbps, variants[i].Boost, cudaGpu, ct, progress).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException) when (i < variants.Count - 1)
            {
                // driver recusou um recurso do reforço — tenta a variante mais enxuta
            }
        }
    }

    private async Task EncodeWithVariantAsync(
        string sourcePath, JobItemRef item, string outputPath, string passLogBase, double duration,
        CodecEncodeConfig cfg, int kbps, NvencBoost boost, int? cudaGpu,
        CancellationToken ct, IProgress<EncodeProgress>? progress)
    {
        var (videoArgs, twoPass) = BuildVideoArgs(cfg, kbps, cudaGpu, boost);

        // 2-pass (SVT): primeira passada — com preset turbo (mais rápido) se configurado
        if (twoPass)
        {
            var pass1Cfg = cfg.TurboFirstPass
                ? cfg with { Preset = Math.Min(13, cfg.Preset + 6) }
                : cfg;
            var (pass1Args, _) = BuildVideoArgs(pass1Cfg, kbps);
            await RunFfmpegAsync(BuildArgs(sourcePath, item, null, pass1Args, passLogBase, 1, null, duration), ct, null, 0).ConfigureAwait(false);
        }

        var (mainArgs, _) = BuildVideoArgs(cfg, kbps, cudaGpu, boost);
        await RunFfmpegAsync(BuildArgs(sourcePath, item, outputPath, mainArgs, passLogBase, twoPass ? 2 : 0, progress, duration), ct, progress, duration).ConfigureAwait(false);
    }

    /// <summary>Referência leve a um trecho (evita depender da entidade EF aqui).</summary>
    public record JobItemRef(double StartSeconds, double EndSeconds, int TargetKbps);

    private static string[] BuildArgs(
        string sourcePath, JobItemRef item, string? outputPath,
        IReadOnlyList<string> videoArgs, string passLogBase, int pass, IProgress<EncodeProgress>? progress, double duration)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-ss", item.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-t", (item.EndSeconds - item.StartSeconds).ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-map", "0:a:0?",
        };

        if (pass > 0)
        {
            args.AddRange(["-pass", pass.ToString()]);
            args.AddRange(["-passlogfile", passLogBase]);
        }

        // Partes NUNCA carregam os capítulos do arquivo original (regra do app:
        // o final recebe só os capítulos gerados na tela de Episódios).
        args.AddRange(["-map_chapters", "-1"]);

        args.AddRange(videoArgs);
        // Áudio re-encodado em AAC 160k (como no script original): stream-copy quebra o corte
        // -t/-ss (o áudio copiado estoura a duração da parte e desalinha os capítulos).
        args.AddRange(["-c:a", "aac", "-b:a", "160k"]);

        // IMPORTANTE: no ffmpeg as opções valem para o PRÓXIMO arquivo da linha — a saída
        // tem que vir SEMPRE POR ÚLTIMO. Antes, o pass 1 terminava em "-f null NUL" ANTES
        // dos -c:v/-b:v; o encoder nunca era acionado, o statsfile nascia vazio e o pass 2
        // morria com "Invalid stats file size".
        // -progress em TODAS as passadas (inclusive a 1ª, que não reporta nada à UI): é a
        // fonte de "sinal de vida" que o watchdog de stall consome. Metadados apenas — não
        // muda nada no encode. Vai ANTES do arquivo de saída, que tem que ser o último arg.
        args.AddRange(["-progress", "pipe:1", "-nostats"]);

        if (pass == 1)
        {
            args.AddRange(["-an", "-f", "null", NulDevice()]);
        }
        else
        {
            args.Add(outputPath!);
        }

        return [.. args];
    }

    /// <summary>
    /// Interpreta "out_time_us=123" / "out_time_ms=123" (o out_time_ms do ffmpeg também é
    /// microssegundo, apesar do nome) e devolve os segundos convertidos. Retorna false em
    /// qualquer outra linha — inclusive vazias.
    /// </summary>
    public static bool TryParseOutTimeSeconds(string? line, out double outSeconds)
    {
        outSeconds = 0;
        if (string.IsNullOrEmpty(line))
            return false;

        foreach (var prefix in new[] { "out_time_us=", "out_time_ms=" })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(line[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            {
                outSeconds = micros / 1_000_000.0;
                return true;
            }
        }

        return false;
    }

    /// <summary>Atalho de percentual a partir do out_time (mantido pros testes).</summary>
    public static bool TryParseProgressPercent(string? line, double duration, out double percent)
    {
        percent = 0;
        if (!TryParseOutTimeSeconds(line, out var seconds) || duration <= 0)
            return false;

        percent = Math.Clamp(seconds / duration * 100.0, 0, 100);
        return true;
    }

    private static string NulDevice() =>
        OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private async Task RunFfmpegAsync(string[] args, CancellationToken ct, IProgress<EncodeProgress>? progress, double duration)
    {
        using var proc = ProcessRunner.Start(_ffmpeg, args);

        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        var stallCts = new CancellationTokenSource();
        // Qualquer linha na stdout é sinal de vida (-progress emite pares chave=valor o
        // tempo todo, inclusive na passada 1). Escrita sem volatile: o pior caso de corrida
        // é o watchdog ver um atraso maior um ciclo depois — sem consequência.
        var lastActivityTicks = Environment.TickCount64;
        var stalled = false;

        async Task PumpOutputAsync()
        {
            // -progress pipe:1 → pares chave=valor no stdout
            var fps = 0.0;
            var speed = 0.0;
            while (await proc.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lastActivityTicks = Environment.TickCount64;
                if (progress is null || duration <= 0)
                    continue;

                if (line.StartsWith("fps=", StringComparison.Ordinal) &&
                    double.TryParse(line[4..].TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps))
                {
                    fps = parsedFps;
                }
                else if (line.StartsWith("speed=", StringComparison.Ordinal) &&
                         double.TryParse(line[6..].TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed))
                {
                    speed = parsedSpeed;
                }
                else if (TryParseOutTimeSeconds(line, out var outSeconds))
                {
                    progress.Report(new EncodeProgress(fps, speed, outSeconds));
                }
            }
        }

        async Task WatchStallAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(StallPollIntervalMs, stallCts.Token).ConfigureAwait(false);
                    if (proc.HasExited)
                        return;
                    if (Environment.TickCount64 - lastActivityTicks > _stallTimeout.TotalMilliseconds)
                    {
                        stalled = true;
                        ProcessRunner.TryKill(proc);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // encerramento normal — o watchdog só existe enquanto o ffmpeg roda
            }
        }

        var pumpTask = PumpOutputAsync();
        var watchdogTask = WatchStallAsync();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.TryKill(proc);
            throw;
        }
        finally
        {
            stallCts.Cancel();
            await watchdogTask.ConfigureAwait(false);
        }

        await pumpTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        // ffmpeg sem saída além do tempo de stall: morreu por watchdog (ou saiu no mesmo
        // instante em que disparou — aí o código 0/1 manda e tratamos como saída comum).
        if (stalled && proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg ficou {Math.Round(_stallTimeout.TotalMinutes)} min sem produzir saída — morto por provável travamento: {ProcessRunner.Truncate(stderr, 400)}");

        // mkvmerge aceita 1 como warning; ffmpeg não: qualquer código != 0 é erro
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(stderr, 800)}");
    }
}
