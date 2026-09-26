using AnimeBatch.App.Services;
using AnimeBatch.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace AnimeBatch.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Localize();

        if (AppServices.FatalError is { } fatal)
        {
            FatalInfo.Message = fatal;
            FatalInfo.IsOpen = true;
        }

        UpdateDbInfo();

        // Créditos das ferramentas embarcadas: projeto + site + papel (as exe já vêm no pacote)
        AddCreditRow("FFmpeg / ffprobe", AppServices.Tools.FfmpegPath is not null && AppServices.Tools.FfprobePath is not null,
            "settings.toolFfmpeg", "https://ffmpeg.org");
        AddCreditRow("MKVToolNix (mkvmerge / mkvextract)", AppServices.Tools.MkvMergePath is not null && AppServices.Tools.MkvExtractPath is not null,
            "settings.toolMkvtoolnix", "https://mkvtoolnix.download");
        AddCreditRow("HandBrake (HandBrakeCLI)", AppServices.Tools.HandBrakeCliPath is not null,
            "settings.toolHandbrake", "https://handbrake.fr");
        AddCreditRow("SVT-AV1 (SvtAv1EncApp)", AppServices.Tools.SvtAv1EncAppPath is not null,
            "settings.toolSvtav1", "https://gitlab.com/AOMediaCodec/SVT-AV1");
        AddCreditRow("Av1an", AppServices.Tools.Av1anPath is not null,
            "settings.toolAv1an", "https://github.com/rust-av/av1an");
        AddCreditRow("VapourSynth (vspipe)", AppServices.Tools.VspipePath is not null,
            "settings.toolVapoursynth", "https://www.vapoursynth.com");
        AddCreditRow("Real-CUGAN (ncnn Vulkan)", AppServices.Tools.RealCuganPath is not null,
            "settings.toolRealcugan", "https://github.com/nihui/realcugan-ncnn-vulkan");
        AddCreditRow("Real-ESRGAN (ncnn Vulkan)", AppServices.Tools.RealesrganPath is not null,
            "settings.toolRealesrgan", "https://github.com/xinntao/Real-ESRGAN");
        AddCreditRow("AnimeJaNai (modelos ONNX)", AppServices.Tools.OnnxModelsDir is not null,
            "settings.toolAnimejanai", "https://github.com/the-database/AnimeJaNai");

        // Idiomas disponíveis na pasta i18n do app
        var localizer = AppServices.Localizer;
        CmbLanguage.ItemsSource = localizer.Languages
            .Select(l => $"{l.Name}  ({l.Code} · v{l.Version})")
            .ToList();
        var idx = localizer.Languages.ToList().FindIndex(l => l.Code == localizer.CurrentCode);
        _suppressLanguageEvents = true;
        CmbLanguage.SelectedIndex = idx >= 0 ? idx : 0;
        _suppressLanguageEvents = false;

        var t = AppServices.Localizer;

        // Pastas de origem e destino
        SourceDirBox.LostFocus += async (_, _) =>
        {
            var path = SourceDirBox.Text.Trim();
            await AppServices.Settings.SetAsync(SettingsRepository.SourceDirectory, path);
            SourceDirHint.Text = path.Length == 0
                ? t.T("settings.sourceDirHintEmpty")
                : t.T("settings.sourceDirHint");
        };

        OutDirBox.LostFocus += async (_, _) => await SaveOutputDirAsync();

        // GPUs de upscaling: vazio = automático (detecta as NVIDIA pelo próprio upscaler)
        UpscaleGpusBox.PlaceholderText = "auto";
        UpscaleGpusBox.LostFocus += async (_, _) =>
        {
            var value = UpscaleGpusBox.Text.Trim();
            await AppServices.Settings.SetAsync(SettingsRepository.UpscaleGpus, value);
            UpscaleGpusHint.Text = t.T("settings.upscaleGpusHint") + $"  ({t.T("settings.saved")})";
        };
        // Preferências do banco (chave TMDB, pastas, GPUs legadas) carregam async —
        // ctor não pode bloquear em I/O
        Ui.Safe(LoadPreferencesAsync, ex =>
        {
            FatalInfo.Severity = InfoBarSeverity.Error;
            FatalInfo.Title = ex.Message;
            FatalInfo.Message = "";
            FatalInfo.IsOpen = true;
        });
        _ = LoadGpuDetectionAsync();
    }

    private async Task LoadPreferencesAsync()
    {
        var key = await AppServices.Settings.GetAsync(SettingsRepository.TmdbKeySetting);
        TmdbKeyBox.Password = key ?? "";
        TmdbKeyStatus.Text = string.IsNullOrEmpty(key)
            ? AppServices.Localizer.T("settings.tmdbKeyStatusNone")
            : AppServices.Localizer.T("settings.tmdbKeyStatusSet");

        SourceDirBox.Text = await AppServices.GetSourceDirectoryAsync() ?? "";
        OutDirBox.Text = await AppServices.GetOutputDirectoryAsync();
        UpscaleGpusBox.Text = await AppServices.Settings.GetAsync(SettingsRepository.UpscaleGpus) ?? "";
    }

    /// <summary>Detecta as GPUs em segundo plano e mostra o resultado nas DUAS numerações:
    /// Vulkan (motor ncnn: realcugan/realesrgan) e DXGI/DirectML (motor ONNX: AnimeJaNai).</summary>
    private async Task LoadGpuDetectionAsync()
    {
        var t = AppServices.Localizer;
        var detected = await Core.Services.VulkanGpuProbe
            .EnsureDetectedAsync(AppServices.Tools.RealesrganPath ?? AppServices.Tools.RealCuganPath,
                System.Threading.CancellationToken.None);
        var list = string.Join(", ", detected.Select(d => $"{d.Index} = {d.Name}"));
        var dxgiList = await Task.Run(() => Core.Services.DxgiGpuProbe.Describe());
        DispatcherQueue.TryEnqueue(() =>
        {
            var detectedText = detected.Count > 0
                ? $"{t.T("settings.upscaleGpusDetected")} (Vulkan): {list}"
                : t.T("settings.upscaleGpusNone");
            var dxgiText = $"{t.T("settings.upscaleGpusDetected")} (ONNX/DirectML): {dxgiList}";
            UpscaleGpusHint.Text = $"{t.T("settings.upscaleGpusHint")} {detectedText}. {dxgiText}";
        });
    }

    private async Task SaveOutputDirAsync()
    {
        var path = OutDirBox.Text.Trim();
        if (path.Length == 0)
            return;

        await AppServices.Settings.SetAsync(AnimeBatch.Core.Services.SettingsRepository.OutputDirectory, path);
        OutputDirHint.Text = $"{AppServices.Localizer.T("settings.outputDirHint")}  ({path})";
    }

    private async void BtnBrowseOutDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        OutDirBox.Text = folder.Path;
        await SaveOutputDirAsync();
    }

    private async void BtnBrowseSourceDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        SourceDirBox.Text = folder.Path;
        await AppServices.Settings.SetAsync(SettingsRepository.SourceDirectory, folder.Path);
        SourceDirHint.Text = AppServices.Localizer.T("settings.sourceDirHint");
    }

    private bool _suppressLanguageEvents;

    private void Localize()
    {
        var t = AppServices.Localizer;
        TitleText.Text = t.T("settings.title");
        IntegrationsHeader.Text = t.T("settings.integrations");
        TmdbKeyBox.Header = t.T("settings.tmdbKeyHeader");
        TmdbKeyBox.PlaceholderText = t.T("settings.tmdbKeyPlaceholder");
        BtnSaveTmdbKey.Content = t.T("settings.tmdbSaveKey");
        TmdbKeyHint.Text = t.T("settings.tmdbHint");
        LanguageHeader.Text = t.T("settings.language");
        LanguageHint.Text = t.T("settings.languageHint");
        SourceDirHeader.Text = t.T("settings.sourceDir");
        SourceDirHint.Text = t.T("settings.sourceDirHint");
        OutputDirHeader.Text = t.T("settings.outputDir");
        BtnBrowseOutDir.Content = t.T("settings.outputDirBrowse");
        OutputDirHint.Text = t.T("settings.outputDirHint");
        BtnBrowseSourceDir.Content = t.T("settings.outputDirBrowse");
        UpscaleGpusHeader.Text = t.T("settings.upscaleGpus");
        UpscaleGpusBox.Header = t.T("settings.upscaleGpus");
        UpscaleGpusBox.PlaceholderText = "auto";
        UpscaleGpusHint.Text = t.T("settings.upscaleGpusHint");
        DatabaseHeader.Text = t.T("settings.database");
        BtnClearDb.Content = t.T("settings.dbClear");
        DbClearHint.Text = t.T("settings.dbClearHint");
        ToolsHeader.Text = t.T("settings.creditsHeader");
        // (as descrições/link de cada ferramenta são aplicadas no ctor via AddCreditRow;
        // ao trocar o idioma aqui, refaz as linhas para traduzir na hora)
        if (ToolsPanel.Children.Count > 0)
        {
            ToolsPanel.Children.Clear();
            AddCreditRow("FFmpeg / ffprobe", AppServices.Tools.FfmpegPath is not null && AppServices.Tools.FfprobePath is not null,
                "settings.toolFfmpeg", "https://ffmpeg.org");
            AddCreditRow("MKVToolNix (mkvmerge / mkvextract)", AppServices.Tools.MkvMergePath is not null && AppServices.Tools.MkvExtractPath is not null,
                "settings.toolMkvtoolnix", "https://mkvtoolnix.download");
            AddCreditRow("HandBrake (HandBrakeCLI)", AppServices.Tools.HandBrakeCliPath is not null,
                "settings.toolHandbrake", "https://handbrake.fr");
            AddCreditRow("SVT-AV1 (SvtAv1EncApp)", AppServices.Tools.SvtAv1EncAppPath is not null,
                "settings.toolSvtav1", "https://gitlab.com/AOMediaCodec/SVT-AV1");
            AddCreditRow("Av1an", AppServices.Tools.Av1anPath is not null,
                "settings.toolAv1an", "https://github.com/rust-av/av1an");
            AddCreditRow("VapourSynth (vspipe)", AppServices.Tools.VspipePath is not null,
                "settings.toolVapoursynth", "https://www.vapoursynth.com");
            AddCreditRow("Real-CUGAN (ncnn Vulkan)", AppServices.Tools.RealCuganPath is not null,
                "settings.toolRealcugan", "https://github.com/nihui/realcugan-ncnn-vulkan");
            AddCreditRow("Real-ESRGAN (ncnn Vulkan)", AppServices.Tools.RealesrganPath is not null,
                "settings.toolRealesrgan", "https://github.com/xinntao/Real-ESRGAN");
            AddCreditRow("AnimeJaNai (modelos ONNX)", AppServices.Tools.OnnxModelsDir is not null,
                "settings.toolAnimejanai", "https://github.com/the-database/AnimeJaNai");
        }
    }

    private async void BtnSaveTmdbKey_Click(object sender, RoutedEventArgs e)
    {
        var t = AppServices.Localizer;
        var key = TmdbKeyBox.Password.Trim();
        await AppServices.Settings.SetAsync(AnimeBatch.Core.Services.SettingsRepository.TmdbKeySetting, key);
        TmdbKeyStatus.Text = string.IsNullOrEmpty(key)
            ? t.T("settings.tmdbKeyRemoved")
            : t.T("settings.tmdbKeySaved");
    }

    private async void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvents)
            return;

        var idx = CmbLanguage.SelectedIndex;
        if (idx < 0 || idx >= AppServices.Localizer.Languages.Count)
            return;

        var code = AppServices.Localizer.Languages[idx].Code;
        AppServices.Localizer.SetLanguage(code);
        await AppServices.Settings.SetAsync(AnimeBatch.Core.Services.SettingsRepository.LanguageKey, code);
        Localize(); // re-aplica nesta tela; as demais usam o idioma ao navegar
        (App.MainWindow as MainWindow)?.RefreshLanguage(); // menu lateral também troca na hora
    }

    /// <summary>Linha de crédito: ✓/✗ de presença da exe, nome do projeto, papel dele no app
    /// e link oficial (abre no navegador padrão). Sem caminhos de disco — os binários já vêm
    /// no pacote; o que importa aqui é o crédito.</summary>
    private void AddCreditRow(string name, bool present, string roleKey, string url)
    {
        var t = AppServices.Localizer;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run
        {
            Text = present ? "✓ " : "✗ ",
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        text.Inlines.Add(new Run { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Run
        {
            Text = present
                ? $" — {t.T(roleKey)}"
                : $" — {t.T(roleKey)} ({t.T("settings.toolMissing")})",
        });
        text.Inlines.Add(new Run { Text = "  " });
        var link = new Hyperlink { NavigateUri = new Uri(url) };
        link.Inlines.Add(new Run { Text = url });
        link.Click += (_, _) => OpenUrl(url);
        text.Inlines.Add(link);
        if (!present)
            text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray);

        ToolsPanel.Children.Add(text);
    }

    /// <summary>Abre o link no navegador padrão via Launcher do Windows (sem processo shell).</summary>
    private static async void OpenUrl(string url)
    {
        try
        {
            _ = await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch
        {
            // sem manipulador de URL no sistema — o link ainda é visível para copiar
        }
    }

    // ---------- base de dados ----------

    private void UpdateDbInfo()
    {
        var path = AnimeBatch.Core.Data.AnimeBatchDbContext.DefaultDbPath();
        var sizeMb = File.Exists(path)
            ? $" · {new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB"
            : "";
        DbInfoText.Text = $"{Path.GetFileName(path)}\n{Path.GetDirectoryName(path)}{sizeMb}";
    }

    private async void BtnClearDb_Click(object sender, RoutedEventArgs e)
    {
        var t = AppServices.Localizer;
        if (AppServices.Queue.IsRunning)
        {
            FatalInfo.Severity = InfoBarSeverity.Warning;
            FatalInfo.Title = t.T("settings.dbClearBlocked");
            FatalInfo.Message = "";
            FatalInfo.IsOpen = true;
            return;
        }

        var dialog = new ContentDialog
        {
            Title = t.T("settings.dbClearTitle"),
            Content = t.T("settings.dbClearMsg"),
            CloseButtonText = t.T("episodes.addDialog.cancel"),
            PrimaryButtonText = t.T("settings.dbClearConfirm"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            // Apaga os DADOS (jobs, partes, histórico, séries, palavras-chave) e mantém as
            // preferências (idioma, pastas, chave TMDB, configs de encode). Ordem = FKs.
            await using var db = AppServices.DbFactory();
            await db.JobItems.ExecuteDeleteAsync();
            await db.Jobs.ExecuteDeleteAsync();
            await db.ConversionRecords.ExecuteDeleteAsync();
            await db.Series.ExecuteDeleteAsync();
            await db.Keywords.ExecuteDeleteAsync();

            UpdateDbInfo();
            FatalInfo.Severity = InfoBarSeverity.Success;
            FatalInfo.Title = t.T("settings.dbCleared");
            FatalInfo.Message = "";
            FatalInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            FatalInfo.Severity = InfoBarSeverity.Error;
            FatalInfo.Title = t.T("settings.title");
            FatalInfo.Message = ex.Message;
            FatalInfo.IsOpen = true;
        }
    }
}
