using AnimeBatch.App.Services;
using AnimeBatch.App.ViewModels;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Windows.Storage.Pickers;

namespace AnimeBatch.App.Views;

public sealed partial class EpisodesPage : Page
{
    // Defaults aplicados quando a série não tem cadastro (e usados pra cadastrá-la)
    private const int DefaultEpisodeKbps = 500;
    private const int DefaultOpeningKbps = 1500;
    private const int DefaultEndingKbps = 500;

    /// <summary>Áudio de saída (AAC) — base da estimativa de tamanho de áudio.</summary>
    private const int AudioKbps = 160;

    // ObservableCollection: trocar de pasta dá Clear()+Add() e a lista PRECISA avisar a UI.
    // Com List<T> comum o ListView não rebind (mesma instância no ItemsSource = no-op) e
    // continuava exibindo os arquivos da pasta antiga com o rótulo da nova.
    private readonly ObservableCollection<EpisodeViewModel> _episodes = [];
    private EpisodeViewModel? _selected;
    private NotifyCollectionChangedEventHandler? _chaptersChangedHandler;
    private CodecEncodeConfig? _codecCfg;
    private string? _codecCfgFor;

    public EpisodesPage()
    {
        InitializeComponent();
        Localize();
        _ = LoadDefaultFolderAsync();
    }

    private void Localize()
    {
        var t = AppServices.Localizer;
        TitleText.Text = t.T("episodes.title");
        BtnPickFolder.Content = t.T("episodes.pickFolder");
        ChaptersHeader.Text = t.T("episodes.chapters");
        BtnAddChapter.Content = t.T("episodes.addChapter");
        BtnEnqueue.Content = t.T("episodes.enqueue");
        CmbUpscaleNone.Content = t.T("episodes.upscaleNone");
        CmbUpscaleOnly.Content = t.T("episodes.upscaleOnly");
        CmbUpscaleBoth.Content = t.T("episodes.upscaleBoth");
        CmbUpscaleMode.Header = t.T("episodes.upscaleMode");
        CmbUpscaleResolution.Header = t.T("episodes.resolution");
        CmbUpscaleModel.Header = t.T("episodes.model");
        CmbModelCugan.Content = t.T("episodes.modelCugan");
        CmbModelEsrgan.Content = t.T("episodes.modelEsrgan");
        CmbModelOnnx.Content = t.T("episodes.modelOnnx");
        CmbVideoCodec.Header = t.T("episodes.codec");
        CmbVideoCodec.ItemsSource = VideoCodecOptions.Options.Select(o => o.Label).ToList();
        CmbVideoCodec.SelectedIndex = 3; // AV1 10bits NVENC (padrão pra quem tem GPU)
        UpdateSizeEstimateLabels(0);
    }

    /// <summary>Pasta Origem Vídeos (Configurações): carrega os episódios automaticamente.</summary>
    private async Task LoadDefaultFolderAsync()
    {
        var source = AppServices.GetSourceDirectory();
        if (!string.IsNullOrEmpty(source) && Directory.Exists(source))
            await LoadFolderAsync(source);
    }

    private async void BtnPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await LoadFolderAsync(folder.Path);
    }

    private async Task LoadFolderAsync(string folder)
    {
        FolderLabel.Text = folder;
        _episodes.Clear();
        SetDetailVisible(false);

        var files = Directory.EnumerateFiles(folder)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".mkv" or ".mp4";
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
            _episodes.Add(new EpisodeViewModel(f));

        EpisodesList.ItemsSource = _episodes;
        ShowStatus(_episodes.Count == 0
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Success,
            AppServices.Localizer.T("episodes.found", _episodes.Count),
            _episodes.Count == 0 ? AppServices.Localizer.T("episodes.foundEmptyHint") : null);
        await Task.CompletedTask;
    }

    /// <summary>Capítulos e estimativas só existem com um vídeo selecionado.</summary>
    private void SetDetailVisible(bool visible)
    {
        DetailPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void EpisodesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EpisodesList.SelectedItem is not EpisodeViewModel ep)
        {
            SetDetailVisible(false);
            return;
        }

        _selected = ep;
        SetDetailVisible(true);
        MatchLabel.Text = "";
        EpisodeInfoLabel.Text = "";
        BindChapters(ep);

        if (AppServices.Probe is null)
        {
            ShowStatus(InfoBarSeverity.Error,
                AppServices.Localizer.T("episodes.ffprobeMissingTitle"),
                AppServices.Localizer.T("episodes.ffprobeMissingMsg"));
            return;
        }

        await ProbeEpisodeAsync(ep);
    }

    /// <summary>Liga a coleção de capítulos do episódio à lista, com renumeração ao mover/adicionar/remover.</summary>
    private void BindChapters(EpisodeViewModel ep)
    {
        if (_chaptersChangedHandler is not null && ChaptersList.ItemsSource is ObservableCollection<ChapterItemViewModel> oldCol)
            oldCol.CollectionChanged -= _chaptersChangedHandler;

        _chaptersChangedHandler = (_, _) =>
        {
            ResequenceChapters();
            _ = UpdateSizeEstimateAsync();
        };

        ChaptersList.ItemsSource = ep.Chapters;
        ep.Chapters.CollectionChanged += _chaptersChangedHandler;
        ResequenceChapters();
        _ = UpdateSizeEstimateAsync();
    }

    private IEnumerable<ChapterItemViewModel> Chapters() =>
        ChaptersList.ItemsSource as IEnumerable<ChapterItemViewModel> ?? [];

    private void ResequenceChapters()
    {
        var i = 1;
        foreach (var chapter in Chapters())
            chapter.Number = i++;
    }

    private async Task ProbeEpisodeAsync(EpisodeViewModel ep)
    {
        EpisodeInfo info;
        try
        {
            info = ep.Probed ?? await AppServices.Probe!.ProbeAsync(ep.FullPath);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, $"{AppServices.Localizer.T("episodes.ffprobeMissingTitle")}: {ep.FileName}", ex.Message);
            return;
        }

        ep.Probed = info;
        var keywords = await AppServices.Keywords.GetAllAsync();
        var series = await AppServices.Series.FindByEpisodeAsync(ep.FileName);
        ep.Series = series;

        // Apenas o nome da série (bitrates aparecem por capítulo)
        MatchLabel.Text = series is not null
            ? series.Name
            : AppServices.Localizer.T("episodes.seriesUnregistered");

        EpisodeInfoLabel.Text =
            $"{info.AudioStreams.Count} trilha(s) de áudio · {info.SubtitleStreams.Count} legenda(s) · " +
            $"{info.Chapters.Count} capítulos · {TimeSpan.FromSeconds(info.DurationSeconds):hh\\:mm\\:ss}";

        // Se a lista já foi editada (add/remove/reorder), preserva as edições;
        // só reconstrói a partir dos capítulos originais na primeira sonda.
        if (ep.Chapters.Count == 0)
        {
            var ranges = AppServices.Chapters.BuildRanges(info.Chapters, info.DurationSeconds);
            foreach (var r in ranges)
                ep.Chapters.Add(CreateChapterVM(r.Number, r.Title, r.StartSeconds, r.EndSeconds, series, keywords));
            ResequenceChapters();

            // Arquivo sem capítulos ganhou o default (00:00:00.000 → duração): o rótulo
            // mostra o capítulo efetivo da lista, não o do source
            EpisodeInfoLabel.Text =
                $"{info.AudioStreams.Count} trilha(s) de áudio · {info.SubtitleStreams.Count} legenda(s) · " +
                $"{ep.Chapters.Count} capítulo(s) · {TimeSpan.FromSeconds(info.DurationSeconds):hh\\:mm\\:ss}";
        }

        await UpdateSizeEstimateAsync();
        StatusInfo.IsOpen = false;
    }

    private ChapterItemViewModel CreateChapterVM(int number, string title, double start, double end, Series? series, IReadOnlyList<KeywordRule> keywords)
    {
        var cls = ChapterService.Classify(title, keywords);
        var kbps = KbpsFor(cls, series);
        var chapter = new ChapterItemViewModel
        {
            Number = number,
            Title = title,
            TimeRange = $"{FormatTime(start)} → {FormatTime(end)}",
            Class = cls,
            TargetKbps = kbps,
            TargetLabel = ClassLabel(cls),
            StartSeconds = start,
            EndSeconds = end,
        };
        HookChapter(chapter);
        return chapter;
    }

    /// <summary>Marcas/desmarcas de "incluir" recalculam a estimativa de tamanho.</summary>
    private void HookChapter(ChapterItemViewModel chapter) =>
        chapter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ChapterItemViewModel.Include) or nameof(ChapterItemViewModel.TargetLabel))
                _ = UpdateSizeEstimateAsync();
        };

    private static int KbpsFor(BitrateClass cls, Series? series) => cls switch
    {
        BitrateClass.Opening => series?.OpeningKbps ?? DefaultOpeningKbps,
        BitrateClass.Ending => series?.EndingKbps ?? DefaultEndingKbps,
        // Cena crítica usa o maior bitrate disponível da série (abertura), sem virar capítulo no arquivo final
        BitrateClass.Critical => series?.OpeningKbps ?? DefaultOpeningKbps,
        _ => series?.EpisodeKbps ?? DefaultEpisodeKbps,
    };

    private string ClassLabel(BitrateClass cls) => cls switch
    {
        BitrateClass.Opening => AppServices.Localizer.T("class.opening"),
        BitrateClass.Ending => AppServices.Localizer.T("class.ending"),
        BitrateClass.Critical => AppServices.Localizer.T("class.critical"),
        _ => AppServices.Localizer.T("class.episode"),
    };

    /// <summary>Codec selecionado na barra de configurações (com cache da config da aba Encodes).</summary>
    private async Task<CodecEncodeConfig> GetSelectedCodecConfigAsync()
    {
        var code = VideoCodecOptions.Options[
            Math.Clamp(CmbVideoCodec.SelectedIndex, 0, VideoCodecOptions.Options.Length - 1)].Code;
        if (_codecCfgFor != code)
        {
            _codecCfg = await AppServices.EncodeConfigs.GetAsync(code);
            _codecCfgFor = code;
        }

        return _codecCfg!;
    }

    private async void CmbVideoCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Trocou o codec: rótulos dos capítulos (CQ esconde kbps) e estimativas mudam
        if (_selected?.Probed is null)
            return;

        var cfg = await GetSelectedCodecConfigAsync();
        foreach (var c in Chapters())
            c.TargetLabel = BuildTargetLabel(c.Class, c.TargetKbps, cfg);

        await UpdateSizeEstimateAsync();
    }

    private string BuildTargetLabel(BitrateClass cls, int kbps, CodecEncodeConfig cfg) =>
        cfg.UseConstantQuality
            ? ClassLabel(cls)
            : $"{ClassLabel(cls)} · {kbps} kbps";

    private void UpscaleMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Dispara durante o parse do XAML (SelectedIndex no markup), quando os outros
        // combos ainda não existem — guarda de null obrigatória.
        if (CmbUpscaleResolution is null || CmbUpscaleModel is null)
            return;

        var hasUpscale = CmbUpscaleMode.SelectedIndex > 0;
        CmbUpscaleResolution.IsEnabled = hasUpscale;
        // O modelo só aparece com upscale escolhido (e vai por último na row, depois do codec)
        CmbUpscaleModel.Visibility = hasUpscale ? Visibility.Visible : Visibility.Collapsed;
    }

    private (UpscaleMode Mode, string Model, int TargetHeight, string Codec) UpscaleSelection() => new(
        CmbUpscaleMode.SelectedIndex switch
        {
            1 => UpscaleMode.Only,
            2 => UpscaleMode.WithEncode,
            _ => UpscaleMode.None,
        },
        CmbUpscaleModel.SelectedIndex switch
        {
            1 => "realesrgan",
            2 => "onnx",
            _ => "realcugan",
        },
        CmbUpscaleResolution.SelectedIndex switch
        {
            0 => 720,
            2 => 1440,
            3 => 2160,
            _ => 1080,
        },
        VideoCodecOptions.Options[Math.Clamp(CmbVideoCodec.SelectedIndex, 0, VideoCodecOptions.Options.Length - 1)].Code);

    /// <summary>Série sem cadastro é cadastrada na hora, com os defaults usados na fila.</summary>
    private async Task<Series> EnsureSeriesAsync(EpisodeViewModel ep)
    {
        if (ep.Series is not null)
            return ep.Series;

        var series = new Series
        {
            Name = ChapterService.StripEpisodeSuffix(ep.FileName),
            EpisodeKbps = DefaultEpisodeKbps,
            OpeningKbps = DefaultOpeningKbps,
            EndingKbps = DefaultEndingKbps,
        };
        series = await AppServices.Series.UpsertAsync(series);
        ep.Series = series;
        return series;
    }

    // ---------- edição de capítulos ----------

    private void RemoveChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ChapterItemViewModel item } &&
            ChaptersList.ItemsSource is ObservableCollection<ChapterItemViewModel> col)
        {
            col.Remove(item);
        }
    }

    private async void EditChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChapterItemViewModel item } ||
            ChaptersList.ItemsSource is not ObservableCollection<ChapterItemViewModel> col)
            return;

        var t = AppServices.Localizer;
        var title = new TextBox { Header = t.T("episodes.addDialog.name"), Text = item.Title };
        var start = new TextBox { Header = t.T("episodes.addDialog.start"), Text = FormatTime(item.StartSeconds) };
        var end = new TextBox { Header = t.T("episodes.addDialog.end"), Text = FormatTime(item.EndSeconds) };
        var kbps = new NumberBox
        {
            Header = t.T("episodes.addDialog.kbps"),
            Value = item.TargetKbps,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 50,
            Minimum = 0,
        };
        var error = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(title);
        panel.Children.Add(start);
        panel.Children.Add(end);
        panel.Children.Add(kbps);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = t.T("episodes.editDialog.title"),
            Content = panel,
            CloseButtonText = t.T("episodes.addDialog.cancel"),
            PrimaryButtonText = t.T("episodes.editDialog.save"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (!TryParseTime(start.Text, out var startSeconds) || !TryParseTime(end.Text, out var endSeconds) || endSeconds <= startSeconds)
            {
                args.Cancel = true;
                error.Text = t.T("episodes.addDialog.invalid");
                error.Visibility = Visibility.Visible;
                return;
            }

            var kbpsValue = double.IsNaN(kbps.Value) ? item.TargetKbps : (int)kbps.Value;
            var newTitle = string.IsNullOrWhiteSpace(title.Text) ? item.Title : title.Text.Trim();

            await Task.Yield(); // deixa o dialog fechar antes de mexer na coleção
            var index = col.IndexOf(item);
            if (index < 0)
                return;

            // substitui na mesma posição; classe mantida (reclassificar só ocorre na sonda inicial)
            var updated = new ChapterItemViewModel
            {
                Number = item.Number,
                Title = newTitle,
                TimeRange = $"{FormatTime(startSeconds)} → {FormatTime(endSeconds)}",
                Class = item.Class,
                TargetKbps = kbpsValue,
                TargetLabel = BuildTargetLabel(item.Class, kbpsValue, await GetSelectedCodecConfigAsync()),
                StartSeconds = startSeconds,
                EndSeconds = endSeconds,
                Include = item.Include,
            };
            HookChapter(updated);
            col[index] = updated;
        };

        _ = dialog.ShowAsync();
    }

    private void BtnAddChapter_Click(object sender, RoutedEventArgs e)
    {
        if (ChaptersList.ItemsSource is not ObservableCollection<ChapterItemViewModel>)
            return;

        var t = AppServices.Localizer;
        var query = new TextBox { PlaceholderText = t.T("episodes.newChapter"), Text = t.T("episodes.newChapter") };
        var start = new TextBox { PlaceholderText = "0:00.000" };
        var last = Chapters().LastOrDefault();
        if (last is not null)
            start.Text = FormatTime(last.EndSeconds);
        var end = new TextBox { PlaceholderText = "1:00" };
        var kbps = new NumberBox
        {
            Value = _selected?.Series?.EpisodeKbps ?? DefaultEpisodeKbps,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 50,
            Minimum = 0,
        };
        var error = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(query);
        panel.Children.Add(start);
        panel.Children.Add(end);
        panel.Children.Add(kbps);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = t.T("episodes.addDialog.title"),
            Content = panel,
            CloseButtonText = t.T("episodes.addDialog.cancel"),
            PrimaryButtonText = t.T("episodes.addDialog.add"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (!TryParseTime(start.Text, out var startSeconds) || !TryParseTime(end.Text, out var endSeconds) || endSeconds <= startSeconds)
            {
                args.Cancel = true;
                error.Text = t.T("episodes.addDialog.invalid");
                error.Visibility = Visibility.Visible;
                return;
            }

            var kbpsValue = double.IsNaN(kbps.Value) ? DefaultEpisodeKbps : (int)kbps.Value;
            var chapter = new ChapterItemViewModel
            {
                Title = string.IsNullOrWhiteSpace(query.Text) ? t.T("episodes.newChapter") : query.Text.Trim(),
                TimeRange = $"{FormatTime(startSeconds)} → {FormatTime(endSeconds)}",
                Class = BitrateClass.Episode,
                TargetKbps = kbpsValue,
                TargetLabel = BuildTargetLabel(BitrateClass.Episode, kbpsValue, await GetSelectedCodecConfigAsync()),
                StartSeconds = startSeconds,
                EndSeconds = endSeconds,
            };
            HookChapter(chapter);

            await Task.Yield(); // deixa o dialog fechar antes de mexer na coleção
            if (ChaptersList.ItemsSource is ObservableCollection<ChapterItemViewModel> col)
                col.Add(chapter);
        };

        _ = dialog.ShowAsync();
    }

    /// <summary>Aceita "ss.fff", "mm:ss(.fff)" e "hh:mm:ss(.fff)" — minutos e segundos por intuição, não TimeSpan.</summary>
    private static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var v = text.Trim();
        var parts = v.Split(':');
        if (parts.Length is < 1 or > 3)
            return false;

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
                return false;
            total = total * 60 + value;
        }

        seconds = total;
        return true;
    }

    // ---------- estimativa de tamanho ----------

    private void UpdateSizeEstimateLabels(double videoBytes)
    {
        var t = AppServices.Localizer;
        SizeVideoLine.Text = t.T("episodes.sizeVideo", videoBytes > 0 ? FormatSize((long)videoBytes) : "—");
    }

    private async Task UpdateSizeEstimateAsync()
    {
        var selected = Chapters().Where(c => c.Include).ToList();
        var totalDuration = selected.Sum(c => Math.Max(0, c.EndSeconds - c.StartSeconds));

        var cfg = await GetSelectedCodecConfigAsync();
        var cqMode = cfg.UseConstantQuality;

        // kbps × segundos / 8 = kilobytes do trecho (só vídeo). No modo CQ não dá pra estimar.
        var videoKb = cqMode
            ? 0
            : selected.Sum(c => c.TargetKbps * Math.Max(0, c.EndSeconds - c.StartSeconds) / 8.0);
        var audioKb = AudioKbps * totalDuration / 8.0;

        var t = AppServices.Localizer;
        // No modo CQ não há bitrate → sem estimativa de vídeo e sem total
        SizeVideoLine.Text = t.T("episodes.sizeVideo", cqMode ? "—" : FormatSize((long)(videoKb * 1024)));
        SizeAudioLine.Text = t.T("episodes.sizeAudio", FormatSize((long)(audioKb * 1024)));
        SizeTotalLine.Text = cqMode ? "—" : t.T("episodes.sizeTotal", FormatSize((long)((videoKb + audioKb) * 1024)));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    // ---------- enfileirar ----------

    private async void BtnEnqueue_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Probe is null)
        {
            ShowStatus(InfoBarSeverity.Error, AppServices.Localizer.T("episodes.ffprobeMissingTitle"), null);
            return;
        }

        var checkedEpisodes = _episodes.Where(x => x.Include).ToList();
        if (checkedEpisodes.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning,
                AppServices.Localizer.T("episodes.markFiles"),
                AppServices.Localizer.T("episodes.markFilesHint"));
            return;
        }

        var (mode, model, height, codec) = UpscaleSelection();
        BtnEnqueue.IsEnabled = false;
        var totalParts = 0;
        var enqueued = 0;
        var registered = 0;

        foreach (var ep in checkedEpisodes)
        {
            await ProbeEpisodeAsync(ep);
            if (ep.Probed is null)
                continue; // falhou o probe — pula e segue

            // Ordem de exibição da lista (com edições do usuário) é a ordem das partes;
            // capítulos originais do arquivo NÃO vão pro arquivo gerado.
            var chapters = ep.Chapters.Where(c => c.Include).ToList();
            if (chapters.Count == 0)
                continue;

            var wasUnknown = ep.Series is null;
            var series = await EnsureSeriesAsync(ep);
            if (wasUnknown)
                registered++;

            var job = new Job
            {
                SourcePath = ep.FullPath,
                SeriesName = series.Name,
                EpisodeKbps = series.EpisodeKbps,
                OpeningKbps = series.OpeningKbps,
                EndingKbps = series.EndingKbps,
                UpscaleMode = mode,
                UpscaleModel = mode == UpscaleMode.None ? null : model,
                UpscaleTargetHeight = mode == UpscaleMode.None ? null : height,
                VideoCodec = codec,
            };

            var order = 1;
            foreach (var c in chapters)
            {
                job.Items.Add(new JobItem
                {
                    Order = order++,
                    Title = c.Title,
                    StartSeconds = c.StartSeconds,
                    EndSeconds = c.EndSeconds,
                    Class = c.Class,
                    TargetKbps = c.TargetKbps,
                });
            }

            await AppServices.Jobs.AddAsync(job);
            enqueued++;
            totalParts += job.Items.Count;
        }

        BtnEnqueue.IsEnabled = true;
        var t = AppServices.Localizer;
        var upscaleInfo = mode == UpscaleMode.None ? "" : t.T("episodes.upscaleInfo", model, height);
        EnqueueStatus.Text = enqueued == 0
            ? t.T("episodes.nothingEnqueued")
            : t.T("episodes.enqueued", enqueued, totalParts, upscaleInfo) +
              (registered > 0 ? t.T("episodes.newSeries", registered) : "");

        ShowStatus(enqueued == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            enqueued == 0 ? t.T("episodes.nothingEnqueued") : t.T("queue.title"),
            EnqueueStatus.Text);
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss\.fff") : ts.ToString(@"m\:ss\.fff");
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string? message)
    {
        StatusInfo.Severity = severity;
        StatusInfo.Title = title;
        StatusInfo.Message = message ?? "";
        StatusInfo.IsOpen = true;
    }
}
