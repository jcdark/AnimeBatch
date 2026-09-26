using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeBatch.Core.Services;

/// <summary>Instantâneo do andamento do encode, lido do -progress do ffmpeg.
/// Pass > 0 indica a passada corrente de um encode multipass (SVT 2-pass): 1 = análise
/// (-f null), 2 = passada final; 0 = encode de passada única (ou NVENC, que nunca é 2-pass).
/// ChunksDone/ChunksTotal/Phase são do motor Av1an: contador de chunks concluídos/total e a
/// fase corrente ("scenes" = análise de cenas, "segmenting" = segmentação, "chunks" =
/// encodeando). No av1an NÃO existe fase "passo 1/2": as duas passadas rodam dentro de
/// cada chunk, em paralelo — quem mostra isso é o contador de chunks.</summary>
public record EncodeProgress(double Fps, double Speed, double OutTimeSeconds, int Pass = 0,
    int ChunksDone = 0, int ChunksTotal = 0, string Phase = "");

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
public class EncodeService : Queueing.IEncodeStage
{
    /// <summary>Tempo sem NENHUMA linha de saída do ffmpeg antes de matar o processo por
    /// stall (evita worker preso a noite inteira num encode travado). Configurável via
    /// setting "queue.stallMinutes"; o default cobre encodes lentos sem falsos positivos.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(10);

    private const int StallPollIntervalMs = 15_000;

    private readonly string _ffmpeg;
    private readonly string? _handBrakeCli;
    private readonly string? _av1an;
    private readonly string? _svtEncApp;
    private readonly string? _av1anEnvPath;
    private readonly string? _ffprobe;
    private readonly TimeSpan _stallTimeout;

    /// <summary>Intervalo do tick do tail do logfile do av1an (1s em produção; injetável nos testes).</summary>
    private readonly int _av1anTailTickMs;

    /// <summary>Plugin BestSource do VapourSynth presente na máquina: chunks VS frame-exatos
    /// (-m bestsource) em vez da segmentação por ffmpeg (fase silenciosa de minutos).</summary>
    private readonly bool _av1anBestSource;

    /// <summary>handBrakeCliPath: quando presente, TODOS os encodes passam pelo HandBrakeCLI
    /// (medido em 18/09: qualidade idêntica ao ffmpeg alinhado, mas é o motor do script
    /// original e aplica defaults de wrapper melhores — tune VQ no SVT). Sem o caminho,
    /// cai no encoder ffmpeg embutido (escada NVENC + 2-pass stats).</summary>
    /// av1anPath/svtAv1EncAppPath: motor Av1an (códigos "av1an_*") — fatia a parte por
    /// cena e encodeia os chunks em paralelo com o SvtAv1EncApp; exige VapourSynth+Python
    /// na máquina (o av1an carrega a VSScript API) e o PATH extra em av1anEnvPath para
    /// encontrar vspipe/ffmpeg/SvtAv1EncApp. ffprobePath serve para medir a duração da
    /// origem e decidir o pré-corte (o av1an rust não tem --trim).
    public EncodeService(string ffmpegPath, TimeSpan? stallTimeout = null, string? handBrakeCliPath = null,
        string? av1anPath = null, string? svtAv1EncAppPath = null, string? av1anEnvPath = null, string? ffprobePath = null,
        int? av1anTailTickMs = null, bool av1anBestSource = false)
    {
        _ffmpeg = ffmpegPath;
        _handBrakeCli = handBrakeCliPath;
        _av1an = av1anPath;
        _svtEncApp = svtAv1EncAppPath;
        _av1anEnvPath = av1anEnvPath;
        _ffprobe = ffprobePath;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _av1anTailTickMs = av1anTailTickMs ?? 1000;
        _av1anBestSource = av1anBestSource;
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

        // AV1 4:2:0 só existe no perfil 0 (Main): 1=High é 4:4:4 e 2=Professional é 4:2:2/12-bit
        // — o SVT 4.x rejeita com "bad parameter" e o ffmpeg sai com código -22 (aconteceu de
        // verdade: perfil Professional salvo de config antiga + SVT novo). Tudo que não é
        // none/0 vai como Main; o combo da aba Encodes agora só oferece None/Main.
        var profileValue = cfg.Profile switch
        {
            "none" => null,
            "1" or "2" => "0",
            var p => p,
        };
        if (profileValue is { } prof)
            args.AddRange(["-profile:v", prof]);

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

    /// <summary>
    /// Monta a linha do HandBrakeCLI — o mesmo padrão do converter.py original: corte em
    /// segundos (--start-at / --stop-at duration), encoder pelo código do codec (os nomes
    /// do app coincidem com os do HB: svt_av1_10bit, nvenc_av1_10bit, …), preset 1–13,
    /// -b no modo taxa de bits com --multi-pass [+/--turbo] ou --quality no modo CQ,
    /// áudio 1 → av_aac 160k. Tune/perfil/reforço ficam DE PROPÓSITO fora: os defaults do
    /// wrapper do HB (tune VQ no SVT) mediram tão bons quanto qualquer ajuste fino.
    /// </summary>
    public static (IReadOnlyList<string> Args, bool MultiPass) BuildHandBrakeArgs(
        CodecEncodeConfig cfg, int kbps, string sourcePath, JobItemRef item, string outputPath)
    {
        var args = new List<string>
        {
            "-i", sourcePath,
            "-o", outputPath,
            "--start-at", $"seconds:{item.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}",
            "--stop-at", $"duration:{(item.EndSeconds - item.StartSeconds).ToString("0.###", CultureInfo.InvariantCulture)}",
            "-e", cfg.Code,
            "--encoder-preset", Math.Clamp(cfg.Preset, 1, 13).ToString(CultureInfo.InvariantCulture),
        };

        var multiPass = false;
        if (cfg.UseConstantQuality)
        {
            args.AddRange(["--quality", cfg.Cq.ToString(CultureInfo.InvariantCulture)]);
        }
        else
        {
            args.AddRange(["-b", kbps.ToString(CultureInfo.InvariantCulture)]);
            multiPass = cfg.Multipass && !cfg.FastConversion;
            if (multiPass)
            {
                args.Add("--multi-pass");
                if (cfg.TurboFirstPass)
                    args.Add("--turbo");
            }
        }

        // Áudio: 1ª trilha → AAC 160k (mesma regra do caminho ffmpeg)
        args.AddRange(["--audio", "1", "--aencoder", "av_aac", "--ab", "160"]);

        return (args, multiPass);
    }

    /// <summary>Progresso do HandBrakeCLI: "Encoding: task 1 of 2, 12.34 % (25.1 fps, avg 24.9 fps, ETA …)".
    /// As linhas vêm separadas por \r (não \n) — o pump fatia manualmente. Task N of M com
    /// M > 1 é a passada do multipass (vira "passo 1/2 · 2/2" no rodapé).</summary>
    private static readonly Regex HandBrakeProgressRegex =
        new(@"Encoding: task (\d+) of (\d+), ([0-9.]+) %(?: \(([\d.]+) fps, avg ([\d.]+) fps)?", RegexOptions.Compiled);

    private async Task RunHandBrakeAsync(IReadOnlyList<string> args, CancellationToken ct, IProgress<EncodeProgress>? progress, double duration)
    {
        using var proc = ProcessRunner.Start(_handBrakeCli!, args);

        var gate = new object();
        var lastActivityTicks = Environment.TickCount64;
        var lastPct = -1.0;
        var smoothPass = -1;
        var smoothPassTick = 0L;
        var smoothPassDoneSeconds = 0.0;
        var stalled = false;
        var tail = new StringBuilder();

        void HandleLine(string line)
        {
            lock (gate)
            {
                lastActivityTicks = Environment.TickCount64;
                tail.AppendLine(line);
                if (tail.Length > 6000)
                    tail.Remove(0, 4000);
            }

            if (progress is null || duration <= 0)
                return;

            var m = HandBrakeProgressRegex.Match(line);
            if (!m.Success ||
                !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                return;

            var pass = 0;
            var taskTotal = 0;
            var taskOf = 0;
            if (int.TryParse(m.Groups[2].Value, out taskTotal) && taskTotal > 1 &&
                int.TryParse(m.Groups[1].Value, out taskOf))
                pass = taskOf;

            // fps MÉDIO do HB (a instantânea pula 20→90 com a cena; a média é estável)
            var fps = 0.0;
            if (m.Groups[5].Success && double.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var avgFps) && avgFps > 0)
                fps = avgFps;
            else if (m.Groups[4].Success)
                double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

            double speed;
            double outTime;
            lock (gate)
            {
                var now = Environment.TickCount64;

                // velocidade = MÉDIA desde o começo da passada (o delta instantâneo entre
                // linhas do HB era ruído puro: 0.0x → 8.0x → 3x entre relatos vizinhos)
                if (pass != smoothPass)
                {
                    smoothPass = pass;
                    smoothPassTick = now;
                    smoothPassDoneSeconds = 0;
                }
                if (lastPct >= 0 && pct > lastPct)
                    smoothPassDoneSeconds += (pct - lastPct) / 100.0 * duration;
                speed = smoothPassDoneSeconds / Math.Max(0.001, (now - smoothPassTick) / 1000.0);
                lastPct = pct;

                // Multipass: só a ÚLTIMA passada produz saída — as anteriores são análise
                // (o ffmpeg/HB escreve em NUL). Sem isso o rodapé somava o vídeo 2x no 2-pass.
                outTime = taskTotal > 1 && taskOf < taskTotal ? 0.0 : pct / 100.0 * duration;
            }

            progress.Report(new EncodeProgress(fps, speed, outTime, pass));
        }

        // stdout e stderr carregam linhas do HB (progresso em um, logs em outro) — os dois
        // alimentam o mesmo tratador; qualquer byte é sinal de vida para o watchdog.
        async Task PumpAsync(TextReader reader)
        {
            var buf = new char[4096];
            var sb = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buf, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buf[i];
                    if (c is '\r' or '\n')
                    {
                        if (sb.Length > 0)
                        {
                            HandleLine(sb.ToString());
                            sb.Clear();
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            if (sb.Length > 0)
                HandleLine(sb.ToString());
        }

        var stallCts = new CancellationTokenSource();

        async Task WatchStallAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(StallPollIntervalMs, stallCts.Token).ConfigureAwait(false);
                    if (proc.HasExited)
                        return;
                    lock (gate)
                    {
                        if (Environment.TickCount64 - lastActivityTicks <= _stallTimeout.TotalMilliseconds)
                            continue;
                    }
                    stalled = true;
                    ProcessRunner.TryKill(proc);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // encerramento normal — o watchdog só existe enquanto o HB roda
            }
        }

        var pumpOut = PumpAsync(proc.StandardOutput);
        var pumpErr = PumpAsync(proc.StandardError);
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

        await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);
        var stderr = "";
        lock (gate)
            stderr = tail.ToString();

        if (stalled && proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"HandBrakeCLI ficou {Math.Round(_stallTimeout.TotalMinutes)} min sem produzir saída — morto por provável travamento: {ProcessRunner.Truncate(stderr, 400)}");

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"HandBrakeCLI falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(stderr, 800)}");
    }

    // ---------------- Av1an (códigos av1an_*) ----------------

    /// <summary>Pré-corte lossless (x264 qp0) quando a parte não é o arquivo inteiro.
    /// -progress pipe:1 é OBRIGATÓRIO aqui: sem ele o stdout fica mudo, o RunFfmpegAsync
    /// não emite relato nenhum (rodapé congelado em FPS 0.0/Elapsed 00:00 — parecia
    /// "não convertendo" durante os ~15 min de corte) e o watchdog de stall ainda podia
    /// matar um corte longo por inatividade.</summary>
    public static string[] BuildPrecutArgs(string sourcePath, double startSeconds, double duration,
        string pixFmt, string outputPath)
    {
        return
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-progress", "pipe:1", "-nostats",
            "-ss", startSeconds.ToString("0.##########", CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-t", duration.ToString("0.##########", CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-map", "0:a:0?", "-map_chapters", "-1",
            "-c:v", "libx264", "-qp", "0", "-preset", "veryfast", "-pix_fmt", pixFmt,
            "-c:a", "aac", "-b:a", "160k",
            outputPath,
        ];
    }

    /// <summary>RAM física estimada por processo do SvtAv1EncApp em 1080p (2-pass VBR,
    /// lookahead 42). Medido com 37 chunks reais: ~2,4 GB no preset 4 e ~2,0 GB no 6 —
    /// IGUAL no stock e no fork. Com -w = núcleos (32 no motor sequencial) o encode pedia
    /// 60-77 GB e máquinas de 48 GB iam a OOM: "allocate memory failed" em cascata e
    /// 0xc0000005 nos chunks (o dono viu isso no AV1 Híbrido IA 10bits p4 em 26/09).
    /// Pega o pior caso + folga; não escala com preset para não depender de medida fina.</summary>
    public const long Av1anWorkerMemoryBudget = 2_750_000_000;

    /// <summary>Workers internos do av1an (chunks simultâneos): divide os núcleos entre as
    /// instâncias que o pool de partes do QueueRunner puser para rodar, MAS limita pelo total
    /// de processos × RAM por processo — sem isso o encode inteiro não cabe na memória.
    /// <paramref name="totalMemoryBytes"/> = RAM física total (GC.GetGCMemoryInfo).</summary>
    public static int Av1anInternalWorkers(int processorCount, int parallelWorkers, long totalMemoryBytes)
    {
        var byCpu = Math.Clamp(processorCount / Math.Max(1, parallelWorkers), 1, Math.Max(1, processorCount));
        if (totalMemoryBytes <= 0)
            return byCpu;
        // 80% da RAM física para os encoders; o total de processos é paralelo × interno
        var byMem = (int)(totalMemoryBytes * 0.8 / (Av1anWorkerMemoryBudget * Math.Max(1, parallelWorkers)));
        return Math.Max(1, Math.Min(byCpu, byMem));
    }

    /// <summary>
    /// Linha do av1an para os códigos av1an_*. --no-defaults SEMPRE: os defaults deles
    /// injetam --crf 25, que conflita com --rc 1/--tbr (o svt 4.x nem abre o encoder), e
    /// usam --keyint 0, que o svt 4.x rejeita em VBR ("intra period must be > 0") — GOP
    /// fixo de 240 frames (10s a 24fps; os cortes de cena do av-scenechange continuam
    /// entrando como keyframes nas bordas dos chunks). CQ (--rc 0 --crf) ou bitrate
    /// (--rc 1 --tbr) e -p 2 quando Multipass+bitrate. pix-format carrega o 8/10 bits.
    /// </summary>
    public static IReadOnlyList<string> BuildAv1anArgs(
        CodecEncodeConfig cfg, int kbps, string inputPath, string outputPath, string tempDir,
        string logfile, int internalWorkers, string audioParams, bool bestSourceChunks = false)
    {
        var pixFmt = cfg.Code.EndsWith("10bit", StringComparison.Ordinal) ? "yuv420p10le" : "yuv420p";
        var twoPass = !cfg.UseConstantQuality && cfg.Multipass && !cfg.FastConversion;
        var rc = cfg.UseConstantQuality
            ? $"--rc 0 --crf {cfg.Cq.ToString(CultureInfo.InvariantCulture)}"
            : $"--rc 1 --tbr {kbps.ToString(CultureInfo.InvariantCulture)}";

        var args = new List<string>
        {
            "-i", inputPath,
            "-o", outputPath,
            "--temp", tempDir,
            "-e", "svt-av1",
            "-w", Math.Max(1, internalWorkers).ToString(CultureInfo.InvariantCulture),
            "-y",
            "-l", logfile,
            "--no-defaults",
            "--pix-format", pixFmt,
        };
        if (twoPass)
            args.AddRange(["-p", "2"]);
        args.AddRange(["-v", $"--keyint 240 --scd 0 --preset {Math.Clamp(cfg.Preset, 1, 13).ToString(CultureInfo.InvariantCulture)} {rc}"]);
        args.AddRange(["-a", audioParams]);

        // Análise de cenas mais rápida (av-scenechange é single-core: a CPU fica ociosa na
        // análise em resolução cheia). Só muda ONDE os chunks cortam — video params intocados.
        switch (cfg.EffectiveScenecutMode)
        {
            case 1:
                args.AddRange(["--sc-downscale-height", "720"]);
                break;
            case 2:
                args.AddRange(["--sc-downscale-height", "360", "--sc-method", "fast"]);
                break;
        }

        // Plugin BestSource do VapourSynth presente: chunks frame-exatos lidos na hora
        // (sem a fase "Segmenting video" do ffmpeg, que era I/O-bound e levava minutos).
        if (bestSourceChunks)
            args.AddRange(["-m", "bestsource"]);

        return args;
    }

    private async Task EncodeWithAv1anAsync(
        string sourcePath, JobItemRef item, string outputPath, string passLogBase,
        CodecEncodeConfig cfg, int kbps, double duration,
        CancellationToken ct, IProgress<EncodeProgress>? progress)
    {
        if (_av1an is null || _svtEncApp is null)
            throw new InvalidOperationException(
                "O codec AV1an precisa de av1an.exe e SvtAv1EncApp.exe em tools\\ e do VapourSynth instalado na máquina.");

        var workDir = Path.GetDirectoryName(outputPath)!;
        var tempDir = Path.Combine(workDir, $"av1an_{Path.GetFileNameWithoutExtension(outputPath)}");
        var logfile = $"{passLogBase}_av1an.log";
        string? precut = null;
        try
        {
            // O av1an rust NÃO tem --trim: quando a parte não é o arquivo inteiro, faz um
            // pré-corte lossless (mesma receita do intermediário de upscale) e entrega o
            // trecho fechado — áudio já em AAC 160k, que o av1an só copia no remux dele.
            var inputPath = sourcePath;
            var audioParams = "-c:a aac -b:a 160k";
            var needsCut = item.StartSeconds > 0.001;
            if (!needsCut && _ffprobe is not null)
            {
                // tolerância de 100ms: intermediários de upscale saem ±1 frame do nominal
                // (grid de frames) — não vale um pré-corte por isso
                var sourceDuration = ProbeDurationSeconds(sourcePath, ct);
                if (sourceDuration is { } d && item.EndSeconds < d - 0.1)
                    needsCut = true;
            }
            if (needsCut)
            {
                precut = Path.Combine(workDir, $"precut_{Path.GetFileNameWithoutExtension(outputPath)}.mkv");
                var pixFmt = cfg.Code.EndsWith("10bit", StringComparison.Ordinal) ? "yuv420p10le" : "yuv420p";
                var cutArgs = BuildPrecutArgs(sourcePath, item.StartSeconds, duration, pixFmt, precut);
                // o pré-corte é um re-encode lossless (x264 qp0) que demora de verdade —
                // relata progresso, senão o rodapé fica congelado nessa fase
                await RunFfmpegAsync(cutArgs, ct, progress, duration).ConfigureAwait(false);
                inputPath = precut;
                audioParams = "-c:a copy";
            }

            // workers internos do av1an (chunks simultâneos): divide os núcleos entre as
            // instâncias que o pool de partes do QueueRunner puser para rodar, com teto
            // pela RAM física (cada SvtAv1EncApp segura ~2,5 GB em 1080p)
            var internalWorkers = Av1anInternalWorkers(
                Environment.ProcessorCount, cfg.EffectiveParallelWorkers,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            var args = BuildAv1anArgs(cfg, kbps, inputPath, outputPath, tempDir, logfile, internalWorkers, audioParams,
                bestSourceChunks: _av1anBestSource);
            await RunAv1anAsync(args, logfile, outputPath, ct, progress, duration).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempDir);
            if (precut is not null)
                TryDelete(precut);
            TryDelete(logfile);
        }
    }

    /// <summary>Roda o av1an com watchdog de stall e progresso lido do logfile (o stderr
    /// fica sem linhas durante chunks longos — a barra de progresso dele só existe em tty).
    /// O relato é contínuo: a cada linha "finished chunk" e a cada tick de 1s do tail —
    /// fps/velocidade são médias desde o primeiro chunk (Av1anProgressTracker), porque o
    /// av1an não emite linha alguma durante um chunk (nem na passada 1 do 2-pass).</summary>
    private async Task RunAv1anAsync(IReadOnlyList<string> args, string logfile, string outputFile, CancellationToken ct, IProgress<EncodeProgress>? progress, double duration)
    {
        var gate = new object();
        var lastActivityTicks = Environment.TickCount64;
        var tail = new StringBuilder();
        var stalled = false;
        var tracker = new Av1anProgressTracker();

        void PublishProgress()
        {
            if (progress is null || duration <= 0)
                return;
            // HEARTBEAT: antes do primeiro "finished chunk" (scenecut + segmentação + 2-pass
            // do chunk inicial podem levar minutos) relata o batimento com a fase corrente —
            // o rodapé precisa mostrar vida e contexto ou parece que nada está convertendo.
            progress.Report(tracker.Snapshot(duration) ?? tracker.Heartbeat());
        }

        void HandleLine(string line)
        {
            lock (gate)
            {
                lastActivityTicks = Environment.TickCount64;
                tail.AppendLine(line);
                if (tail.Length > 6000)
                    tail.Remove(0, 4000);
            }

            if (tracker.FeedLine(line))
                PublishProgress(); // "finished chunk": relato imediato
        }

        // stderr do av1an (INFO de início/fim, erros) alimenta atividade + tail de erro
        async Task PumpAsync(TextReader reader)
        {
            var buf = new char[4096];
            var sb = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buf, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                lock (gate) { lastActivityTicks = Environment.TickCount64; }
                for (var i = 0; i < read; i++)
                {
                    var c = buf[i];
                    if (c is '\r' or '\n')
                    {
                        if (sb.Length > 0)
                        {
                            HandleLine(sb.ToString());
                            sb.Clear();
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            if (sb.Length > 0)
                HandleLine(sb.ToString());
        }

        // logfile: av1an grava started/finished por chunk — fonte de atividade E progresso
        long logOffset = 0;
        var logRemainder = "";
        var stallCts = new CancellationTokenSource();
        using var proc = ProcessRunner.Start(_av1an!, args, configure: psi =>
        {
            var env = psi.EnvironmentVariables;
            // o encoder pode viver em subpasta de tools\ (tools\svt-av1\) — a PASTA dele
            // precisa estar no PATH que o av1an usa para achar os binários externos
            var extra = _av1anEnvPath;
            if (_svtEncApp is not null)
                extra = Path.GetDirectoryName(_svtEncApp) + ";" + extra;
            if (extra is not null)
                env["PATH"] = extra + ";" + (env["PATH"] ?? "");
        });

        void DrainLog()
        {
            try
            {
                if (!File.Exists(logfile))
                    return;
                using var fs = new FileStream(logfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= logOffset)
                    return;
                fs.Seek(logOffset, SeekOrigin.Begin);
                var sb = new StringBuilder(logRemainder);
                var buf = new byte[8192];
                int read;
                while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                    sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                logOffset = fs.Length;
                lock (gate) { lastActivityTicks = Environment.TickCount64; }
                var lines = sb.ToString().Split('\n');
                logRemainder = lines[^1];
                for (var i = 0; i < lines.Length - 1; i++)
                    HandleLine(lines[i].TrimEnd('\r'));
            }
            catch (IOException)
            {
                // log travado momentaneamente (av1an escrevendo) — tenta no próximo tick
            }
        }

        async Task TailLogAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_av1anTailTickMs, stallCts.Token).ConfigureAwait(false);
                    DrainLog();
                    PublishProgress(); // tick de 1s: fps/velocidade vivos entre chunks
                }
            }
            catch (OperationCanceledException)
            {
                // processo terminou — a drenagem final é feita de forma síncrona
            }
        }

        var tailLog = TailLogAsync();

        async Task WatchStallAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(StallPollIntervalMs, stallCts.Token).ConfigureAwait(false);
                    if (proc.HasExited)
                        return;
                    lock (gate)
                    {
                        if (Environment.TickCount64 - lastActivityTicks <= _stallTimeout.TotalMilliseconds)
                            continue;
                    }
                    stalled = true;
                    ProcessRunner.TryKill(proc);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // encerramento normal — o watchdog só existe enquanto o av1an roda
            }
        }

        var pumpOut = PumpAsync(proc.StandardOutput);
        var pumpErr = PumpAsync(proc.StandardError);
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
            try { await tailLog.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        DrainLog(); // linhas gravadas entre o último tick e a saída do processo
        await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);
        string stderr;
        lock (gate)
            stderr = tail.ToString();

        if (stalled && proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"av1an ficou {Math.Round(_stallTimeout.TotalMinutes)} min sem atividade no log — morto por provável travamento: {ProcessRunner.Truncate(stderr, 400)}");
        if (proc.ExitCode != 0)
        {
            // o av1an panica no boot se a máquina não tem o VapourSynth (VSScript API) —
            // traduz o panic em instrução acionável para quem instalou o app pronto
            if (stderr.Contains("VSScript API not available", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O codec AV1an exige o VapourSynth instalado nesta máquina (com Python 3.13 e o wheel do " +
                    "VapourSynth). Instale-os e rode novamente — os demais codecs (HandBrake/SVT/NVENC) não " +
                    "dependem disso.");
            throw new InvalidOperationException(
                $"av1an falhou (código {proc.ExitCode}): {ProcessRunner.Truncate(stderr, 800)}");
        }
        if (!File.Exists(outputFile))
            throw new InvalidOperationException(
                $"av1an saiu com código 0 mas o arquivo não foi gerado: {ProcessRunner.Truncate(stderr, 400)}");
    }

    /// <summary>Duração do container via ffprobe (null se falhar — o chamador degrada).</summary>
    private double? ProbeDurationSeconds(string file, CancellationToken ct)
    {
        if (_ffprobe is null)
            return null;
        var (stdout, ok) = ProcessRunner.Capture(_ffprobe,
            new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file }, 60_000);
        if (!ok ||
            !double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
            d <= 0)
            return null;
        return d;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort: lixo de temp não pode derrubar o job
        }
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

        // Códigos hybrid_* foram removidos na 0.59 (motor sem diferencial prático; o
        // experimento segue documentado no lab\). Um job antigo no banco com esse codec
        // precisa falhar com instrução clara — NUNCA cair silenciosamente em outro motor.
        if (cfg.Code.StartsWith("hybrid", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "O codec 'AV1 Híbrido IA' foi removido nesta versão. Use o 'AV1an 10bits' — mesmo " +
                "encoder de referência, sem o experimento (ver lab\\plano-remocao-hibrido.md).");

        // Motor Av1an (códigos av1an_*): fatiamento por cena + chunks paralelos com o
        // SvtAv1EncApp. Tem que vir ANTES do HandBrake — é escolha explícita do codec.
        if (cfg.Code.StartsWith("av1an", StringComparison.Ordinal))
        {
            await EncodeWithAv1anAsync(sourcePath, item, outputPath, passLogBase, cfg, kbps, duration, ct, progress).ConfigureAwait(false);
            return;
        }

        // Motor primário: HandBrakeCLI (os 4 codecs AV1 — é o que o script original usava
        // e o wrapper dele aplica defaults melhores). cudaGpu não se aplica: o HB não
        // expõe seleção de placa; o paralelismo por workers continua via processos.
        if (_handBrakeCli is not null)
        {
            var (hbArgs, _) = BuildHandBrakeArgs(cfg, kbps, sourcePath, item, outputPath);
            await RunHandBrakeAsync(hbArgs, ct, progress, duration).ConfigureAwait(false);
            return;
        }

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

        // 2-pass (SVT): primeira passada — com preset turbo (mais rápido) se configurado.
        // O -progress já é emitido em toda passada (fonte de sinal de vida do watchdog);
        // aqui a passada 1 passa a REPORTAR também (Pass=1) — antes ela deixava o rodapé
        // congelado — e a 2 reporta Pass=2, que vira "passo 1/2 / 2/2" no rodapé.
        if (twoPass)
        {
            var pass1Cfg = cfg.TurboFirstPass
                ? cfg with { Preset = Math.Min(13, cfg.Preset + 6) }
                : cfg;
            var (pass1Args, _) = BuildVideoArgs(pass1Cfg, kbps);
            await RunFfmpegAsync(BuildArgs(sourcePath, item, null, pass1Args, passLogBase, 1, WithPass(progress, 1, zeroOutTime: true), duration), ct, WithPass(progress, 1, zeroOutTime: true), duration).ConfigureAwait(false);
        }

        var (mainArgs, _) = BuildVideoArgs(cfg, kbps, cudaGpu, boost);
        await RunFfmpegAsync(BuildArgs(sourcePath, item, outputPath, mainArgs, passLogBase, twoPass ? 2 : 0, WithPass(progress, twoPass ? 2 : 0), duration), ct, WithPass(progress, twoPass ? 2 : 0), duration).ConfigureAwait(false);
    }

    /// <summary>Marca cada relato com a passada corrente do encode multipass (SVT 2-pass).
    /// Na passada 1 (zeroOutTime) o out_time é zerado: ela não produz saída — sem isso o
    /// rodapé somava o vídeo duas vezes (passada 1 + passada 2) em jobs multipass.</summary>
    private sealed class PassTag(IProgress<EncodeProgress> inner, int pass, bool zeroOutTime = false) : IProgress<EncodeProgress>
    {
        public void Report(EncodeProgress p)
        {
            if (zeroOutTime)
                p = p with { OutTimeSeconds = 0 };
            inner.Report(p with { Pass = pass });
        }
    }

    private static IProgress<EncodeProgress>? WithPass(IProgress<EncodeProgress>? progress, int pass, bool zeroOutTime = false) =>
        progress is null || pass == 0 ? progress : new PassTag(progress, pass, zeroOutTime);

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
