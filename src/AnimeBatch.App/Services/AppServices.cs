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

    /// <summary>Serviço TMDB pronto pra uso, ou null se a chave não estiver configurada.</summary>
    public static TmdbService? GetTmdb() =>
        Settings.GetAsync(SettingsRepository.TmdbApiKey).GetAwaiter().GetResult() is { Length: > 0 } key
            ? new TmdbService(key)
            : null;

    /// <summary>Som de fim de conversão (tocado quando um job/parte conclui).</summary>
    public static void PlayCompletionSound() =>
        System.Media.SystemSounds.Exclamation.Play();

    /// <summary>
    /// Pasta de destino das conversões: o que o usuário configurou ou, por padrão,
    /// a pasta Vídeos do perfil do Windows.
    /// </summary>
    public static string GetOutputDirectory()
    {
        var configured = Settings.GetAsync(SettingsRepository.OutputDirectory).GetAwaiter().GetResult();
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
    /// começa vazia e o usuário escolhe a pasta.
    /// </summary>
    public static string? GetSourceDirectory()
    {
        var configured = Settings.GetAsync(SettingsRepository.SourceDirectory).GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured) ? null : configured;
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
            Encode = stall => new EncodeService(Tools.FfmpegPath!, stall),
            Merge = () => new MergeService(Tools.MkvMergePath!),
            Upscale = () => new UpscaleService(Tools.FfmpegPath!),
            OutputDirectory = GetOutputDirectory,
            LogCrash = LogCrash,
            CompletionSound = PlayCompletionSound,
        });

        Localizer = new Localization();
        Localizer.Load(Path.Combine(AppContext.BaseDirectory, "i18n"));
        var language = Settings.GetAsync(SettingsRepository.LanguageKey).GetAwaiter().GetResult();
        Localizer.SetLanguage(string.IsNullOrEmpty(language) ? "pt-BR" : language);
    }
}
