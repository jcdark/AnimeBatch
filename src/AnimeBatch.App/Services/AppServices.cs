using AnimeBatch.Core.Data;
using AnimeBatch.Core.Queueing;
using AnimeBatch.Core.Services;

namespace AnimeBatch.App.Services;

/// <summary>Composição raiz: banco local na pasta do app + ferramentas + repositórios.</summary>
internal static class AppServices
{
    public static string? FatalError { get; set; }

    public static ToolsLocator Tools { get; private set; } = null!;
    public static Func<AnimeBatchDbContext> DbFactory { get; private set; } = null!;
    public static SeriesRepository Series { get; private set; } = null!;
    public static KeywordRepository Keywords { get; private set; } = null!;
    public static JobRepository Jobs { get; private set; } = null!;
    public static ConversionRepository Conversions { get; private set; } = null!;
    public static SettingsRepository Settings { get; private set; } = null!;
    public static EncodeConfigRepository EncodeConfigs { get; private set; } = null!;
    public static Localization Localizer { get; private set; } = new();
    public static QueueRunner Queue { get; private set; } = null!;
    public static ProbeService? Probe { get; private set; }
    public static ChapterService Chapters { get; } = new();
    public static ChapterEditsStore ChapterEdits { get; } = new();

    /// <summary>Append de exceções não tratadas em data\crash.log (pasta do app).</summary>
    public static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch
        {
            // nada mais a fazer — evita crash dentro do logger
        }
    }

    /// <summary>Pastas extras de busca de binários ("tools.extraDirs", separadas por ';' ou
    /// quebra de linha). Só entram as que existem no disco; setting ausente = lista vazia.</summary>
    private static string[] ReadToolsExtraDirs(SettingsRepository settings)
    {
        try
        {
            var raw = settings.GetAsync(SettingsRepository.ToolsExtraDirs).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(raw))
                return [];
            return raw.Split([';', '\n', ','],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Directory.Exists)
                .ToArray();
        }
        catch
        {
            return []; // busca de binários é best-effort — nunca derruba o boot
        }
    }

    /// <summary>Serviço TMDB pronto pra uso, ou null se a chave não estiver configurada.
    /// Síncrono: só para caminhos sem await (ctor); handlers novos preferem GetTmdbAsync.</summary>
    public static TmdbService? GetTmdb() =>
        GetTmdbAsync().GetAwaiter().GetResult();

    public static async Task<TmdbService?> GetTmdbAsync()
    {
        var key = await Settings.GetAsync(SettingsRepository.TmdbKeySetting).ConfigureAwait(false);
        return key is { Length: > 0 } ? new TmdbService(key) : null;
    }

    /// <summary>Som de fim de conversão (tocado quando um job/parte conclui).</summary>
    public static void PlayCompletionSound() =>
        System.Media.SystemSounds.Exclamation.Play();

    /// <summary>
    /// Pasta de destino das conversões: o que o usuário configurou ou, por padrão,
    /// a pasta Vídeos do perfil do Windows. Síncrono: só para caminhos sem await
    /// (roda em threadpool na fila); handlers novos preferem GetOutputDirectoryAsync.
    /// </summary>
    public static string GetOutputDirectory() =>
        GetOutputDirectoryAsync().GetAwaiter().GetResult();

    public static async Task<string> GetOutputDirectoryAsync()
    {
        var configured = await Settings.GetAsync(SettingsRepository.OutputDirectory).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrEmpty(videos))
            return videos;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents) ? AppContext.BaseDirectory : documents;
    }

    /// <summary>
    /// Pasta Origem Vídeos (Configurações): null se não configurada — aí a aba Episódios
    /// começa vazia e o usuário escolhe a pasta. Síncrono: só para caminhos sem await.
    /// </summary>
    public static string? GetSourceDirectory() =>
        GetSourceDirectoryAsync().GetAwaiter().GetResult();

    public static async Task<string?> GetSourceDirectoryAsync()
    {
        var configured = await Settings.GetAsync(SettingsRepository.SourceDirectory).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured) ? null : configured;
    }

    /// <summary>Diretórios injetados no PATH do processo av1an (separados por ';'):
    /// tools\ (ffmpeg, SvtAv1EncApp, mkvmerge), a pasta do VapourSynth (vsscript.dll — o
    /// av1an carrega a API por nome) e Scripts do Python (vspipe.exe). Descobre o
    /// site-packages no Python 3.x instalado por usuário (o mais novo primeiro).
    /// Null quando nada existe — o EncodeService reporta o que faltar.</summary>
    private static string? Av1anEnvPath()
    {
        var dirs = new List<string>();
        if (Tools.ToolsDir is { } tools)
            dirs.Add(tools);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pyRoot = Path.Combine(local, "Programs", "Python");
        if (Directory.Exists(pyRoot))
        {
            foreach (var py in Directory.GetDirectories(pyRoot, "Python3*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var vs = Path.Combine(py, "Lib", "site-packages", "vapoursynth");
                if (File.Exists(Path.Combine(vs, "vsscript.dll")))
                    dirs.Add(vs);
                var scripts = Path.Combine(py, "Scripts");
                if (File.Exists(Path.Combine(scripts, "vspipe.exe")))
                    dirs.Add(scripts);
            }
        }
        return dirs.Count == 0 ? null : string.Join(";", dirs);
    }

    private static bool? _av1anBestSource;

    /// <summary>Plugin BestSource do VapourSynth presente? (checagem 1x por execução, cacheada.)
    /// Presente → o av1an usa -m bestsource: chunks VS frame-exatos, sem a fase silenciosa
    /// de segmentação por ffmpeg. Checagem por ARQUIVO, sem executar nada: a wheel pip
    /// instala libbestsource.dll em <python>\Lib\site-packages\vapoursynth\plugins\ — a mesma
    /// pasta de onde o VS R80 carrega plugins de wheel (avalidado: o namespace 'bs' aparece
    /// no core). O python que interessa é o MESMO que fornece a VSScript API do av1an
    /// (tem vapoursynth\vsscript.dll em site-packages); vale também o autoload por usuário.</summary>
    public static bool Av1anHasBestSource => _av1anBestSource ??= ProbeBestSource();

    private static bool ProbeBestSource()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pyRoot = Path.Combine(local, "Programs", "Python");
        if (Directory.Exists(pyRoot))
        {
            foreach (var py in Directory.GetDirectories(pyRoot, "Python3*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(py, "Lib", "site-packages", "vapoursynth", "vsscript.dll")))
                    continue;
                if (File.Exists(Path.Combine(
                        py, "Lib", "site-packages", "vapoursynth", "plugins", "libbestsource.dll")))
                    return true;
            }
        }

        var userAutoload = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VapourSynth", "plugins64", "libbestsource.dll");
        return File.Exists(userAutoload);
    }

    public static void Initialize()
    {
        DbFactory = () => new AnimeBatchDbContext(AnimeBatchDbContext.DefaultDbPath());
        DbInitializer.Initialize(DbFactory);

        // Settings vem ANTES do ToolsLocator: as pastas extras de busca de binários são setting.
        Settings = new SettingsRepository(DbFactory);
        Tools = new ToolsLocator(extraDirs: ReadToolsExtraDirs(Settings));
        if (Tools.FfprobePath is not null)
            Probe = new ProbeService(Tools.FfprobePath);

        Series = new SeriesRepository(DbFactory);
        Keywords = new KeywordRepository(DbFactory);
        Jobs = new JobRepository(DbFactory);
        Conversions = new ConversionRepository(DbFactory);
        EncodeConfigs = new EncodeConfigRepository(Settings);
        // QueueRunner (Core.Queueing) com as implementações reais injetadas: os caminhos só
        // são lidos dentro do runner DEPOIS da validação de ferramentas essenciais.
        Queue = new QueueRunner(new QueueRunnerDeps
        {
            Jobs = Jobs,
            Settings = Settings,
            Series = Series,
            Conversions = Conversions,
            EncodeConfigs = EncodeConfigs,
            Tools = Tools,
            Probe = Probe,
            // HandBrakeCLI é o motor primário de encode (os 4 codecs AV1) — medida em
            // 18/09/2026 igualou o script original; sem o exe, o EncodeService cai no ffmpeg
            Encode = stall => new EncodeService(Tools.FfmpegPath!, stall, Tools.HandBrakeCliPath,
                Tools.Av1anPath, Tools.SvtAv1EncAppPath, Av1anEnvPath(), Tools.FfprobePath,
                av1anBestSource: Av1anHasBestSource),
            Merge = () => new MergeService(Tools.MkvMergePath!),
            Upscale = () => new UpscaleService(Tools.FfmpegPath!),
            // QC de qualidade opcional (VMAF) — só existe com ffmpeg+ffprobe presentes
            Quality = Tools.FfmpegPath is not null && Tools.FfprobePath is not null
                ? () => new QualityCheckService(Tools.FfmpegPath!, Tools.FfprobePath!)
                : null,
            OutputDirectory = GetOutputDirectory,
            LogCrash = LogCrash,
            CompletionSound = PlayCompletionSound,
            // "quando terminar" da fila: ações de energia na hora (thread de fundo serve);
            // "exit" fecha a janela principal na UI thread
            WhenDone = action =>
            {
                if (action == "exit")
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() => App.MainWindow.Close());
                    return;
                }
                SystemPower.Run(action);
            },
        });

        Localizer = new Localization();
        Localizer.Load(Path.Combine(AppContext.BaseDirectory, "i18n"));
        var language = Settings.GetAsync(SettingsRepository.LanguageKey).GetAwaiter().GetResult();
        Localizer.SetLanguage(string.IsNullOrEmpty(language) ? "pt-BR" : language);
    }
}
