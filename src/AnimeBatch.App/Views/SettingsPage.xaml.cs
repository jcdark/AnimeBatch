using AnimeBatch.App.Services;
using AnimeBatch.Core.Services;
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

        DbPathLabel.Text = $"Banco de dados: {AnimeBatch.Core.Data.AnimeBatchDbContext.DefaultDbPath()}";

        AddToolRow("ffmpeg.exe", AppServices.Tools.FfmpegPath, true);
        AddToolRow("ffprobe.exe", AppServices.Tools.FfprobePath, true);
        AddToolRow("mkvmerge.exe", AppServices.Tools.MkvMergePath, true);
        AddToolRow("mkvextract.exe", AppServices.Tools.MkvExtractPath, true);
        AddToolRow("HandBrakeCLI.exe", AppServices.Tools.HandBrakeCliPath, true);
        AddToolRow("realcugan-ncnn-vulkan.exe", AppServices.Tools.RealCuganPath, false);
        AddToolRow("realesrgan-ncnn-vulkan.exe", AppServices.Tools.RealesrganPath, false);

        var key = AppServices.Settings.GetAsync(AnimeBatch.Core.Services.SettingsRepository.TmdbApiKey)
            .GetAwaiter().GetResult();
        TmdbKeyBox.Password = key ?? "";
        TmdbKeyStatus.Text = string.IsNullOrEmpty(key)
            ? AppServices.Localizer.T("settings.tmdbKeyStatusNone")
            : AppServices.Localizer.T("settings.tmdbKeyStatusSet");

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
        SourceDirBox.Text = AppServices.GetSourceDirectory() ?? "";
        SourceDirBox.LostFocus += async (_, _) =>
        {
            var path = SourceDirBox.Text.Trim();
            await AppServices.Settings.SetAsync(SettingsRepository.SourceDirectory, path);
            SourceDirHint.Text = path.Length == 0
                ? t.T("settings.sourceDirHintEmpty")
                : t.T("settings.sourceDirHint");
        };

        OutDirBox.Text = AppServices.GetOutputDirectory();
        OutDirBox.LostFocus += async (_, _) => await SaveOutputDirAsync();

        // GPUs de upscaling: vazio = automático (detecta as NVIDIA pelo próprio upscaler)
        UpscaleGpusBox.Text = AppServices.Settings
            .GetAsync(SettingsRepository.UpscaleGpus).GetAwaiter().GetResult() ?? "";
        UpscaleGpusBox.PlaceholderText = "auto";
        UpscaleGpusBox.LostFocus += async (_, _) =>
        {
            var value = UpscaleGpusBox.Text.Trim();
            await AppServices.Settings.SetAsync(SettingsRepository.UpscaleGpus, value);
            UpscaleGpusHint.Text = t.T("settings.upscaleGpusHint") + $"  ({t.T("settings.saved")})";
        };
        _ = LoadGpuDetectionAsync();
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
        ToolsHeader.Text = t.T("settings.tools");
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
    }

    private async void BtnSaveTmdbKey_Click(object sender, RoutedEventArgs e)
    {
        var t = AppServices.Localizer;
        var key = TmdbKeyBox.Password.Trim();
        await AppServices.Settings.SetAsync(AnimeBatch.Core.Services.SettingsRepository.TmdbApiKey, key);
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

    private void AddToolRow(string name, string? path, bool essential)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };

        text.Inlines.Add(new Run { Text = path is not null ? "✓ " : "✗ ", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        text.Inlines.Add(new Run { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Run
        {
            Text = path is not null ? $" — {path}" : essential ? " — AUSENTE (essencial)" : " — ausente (só upscale; opcional)",
        });
        if (path is null && essential)
            text.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);

        ToolsPanel.Children.Add(text);
    }
}
