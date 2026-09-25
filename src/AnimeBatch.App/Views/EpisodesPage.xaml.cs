using AnimeBatch.App.Services;
using AnimeBatch.App.ViewModels;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Queueing;
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
        BtnResetChapters.Content = t.T("episodes.resetChapters");
        ToolTipService.SetToolTip(BtnResetChapters, t.T("episodes.resetChaptersHint"));
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

    /// <summary>Pasta da última sessão (setting "episodes.lastFolder") vence; sem ela, a
    /// Pasta Origem Vídeos (Configurações). Nenhuma das duas = tela começa vazia.</summary>
    private async Task LoadDefaultFolderAsync()
    {
        var last = await AppServices.Settings.GetAsync(SettingsRepository.EpisodesLastFolder);
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
        {
            await LoadFolderAsync(last);
            return;
        }

        var source = await AppServices.GetSourceDirectoryAsync();
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
        UpdateResetButton();

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

        // memoriza a pasta p/ recarregar na próxima vez que abrir a aba (best-effort:
        // falha de banco não pode impedir a navegação de pastas)
        try
        {
            await AppServices.Settings.SetAsync(SettingsRepository.EpisodesLastFolder, folder);
        }
        catch { }

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
        // o botão de reset aparece logo (o arquivo de edição independe da sonda)
        ep.HasChapterEdits = AppServices.ChapterEdits.ExistsFor(ep.FullPath);
        UpdateResetButton();
        BindChapters(ep);

        if (AppServices.Probe is null)
        {
            ShowStatus(InfoBarSeverity.Error,
                AppServices.Localizer.T("episodes.ffprobeMissingTitle"),
                AppServices.Localizer.T("episodes.ffprobeMissingMsg"));
            return;
        }

        // Probe + leitura de keywords/série tocam banco e processo — falha vira InfoBar, não crash
        Ui.Safe(async () => await ProbeEpisodeAsync(ep),
            ex => ShowStatus(InfoBarSeverity.Error, ep.FileName, ex.Message));
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

    /// <summary>Sufixo " - S01E02" para o cabeçalho dos detalhes; vazio sem tag no arquivo.</summary>
    private static string TagSuffix(string tag) =>
        tag.Length > 0 ? $" - {tag}" : "";

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

        // Nome da série + tag do episódio (SXXEXX extraído do nome do arquivo)
        var tag = System.Text.RegularExpressions.Regex.Match(ep.FileName, @"[sS]\d+[eE]\d+").Value;
        MatchLabel.Text = series is not null
            ? series.Name + TagSuffix(tag)
            : AppServices.Localizer.T("episodes.seriesUnregistered") + TagSuffix(tag);

        EpisodeInfoLabel.Text =
            $"{info.AudioStreams.Count} trilha(s) de áudio · {info.SubtitleStreams.Count} legenda(s) · " +
            $"{info.Chapters.Count} capítulos · {TimeSpan.FromSeconds(info.DurationSeconds):hh\\:mm\\:ss}";

        // Grade de capítulos: arquivo de edição salvo vence; sem edição, constrói da
        // sonda na primeira vez (edições em memória são preservadas)
        if (ep.Chapters.Count == 0)
            await LoadChapterGridAsync(ep, series, info, keywords);

        // Arquivo sem capítulos ganhou o default (00:00:00.000 → duração): o rótulo
        // mostra o capítulo efetivo da lista, não o do source
        EpisodeInfoLabel.Text =
            $"{info.AudioStreams.Count} trilha(s) de áudio · {info.SubtitleStreams.Count} legenda(s) · " +
            $"{ep.Chapters.Count} capítulo(s) · {TimeSpan.FromSeconds(info.DurationSeconds):hh\\:mm\\:ss}";

        UpdateResetButton();
        await UpdateSizeEstimateAsync();
        StatusInfo.IsOpen = false;
    }

    /// <summary>Origem da grade: grade editada salva (chapters-edits) VENCE os capítulos do
    /// vídeo — tempos, títulos, classes, marcação e bitrates por capítulo vêm do arquivo;
    /// sem edição salva, constrói da sonda com a classificação/bitrates da série.</summary>
    private async Task LoadChapterGridAsync(
        EpisodeViewModel ep, Series? series, EpisodeInfo info, IReadOnlyList<KeywordRule> keywords)
    {
        var saved = AppServices.ChapterEdits.Load(ep.FullPath);
        if (saved is { Count: > 0 })
        {
            foreach (var r in saved)
                ep.Chapters.Add(CreateChapterVM(r, series));
            ep.HasChapterEdits = true;
            return;
        }

        var ranges = AppServices.Chapters.BuildRanges(info.Chapters, info.DurationSeconds);
        foreach (var r in ranges)
            ep.Chapters.Add(CreateChapterVM(r.Number, r.Title, r.StartSeconds, r.EndSeconds, series, keywords));
        ep.HasChapterEdits = false;
        await Task.CompletedTask;
    }

    private ChapterItemViewModel CreateChapterVM(ChapterEditRecord r, Series? series)
    {
        var cls = ChapterEditsStore.ParseClass(r.Class);
        var kbps = r.TargetKbps > 0 ? r.TargetKbps : KbpsFor(cls, series);
        var chapter = new ChapterItemViewModel
        {
            Number = r.Number,
            Title = r.Title,
            TimeRange = $"{FormatTime(r.StartSeconds)} → {FormatTime(r.EndSeconds)}",
            Class = cls,
            TargetKbps = kbps,
            TargetLabel = ClassLabel(cls),
            StartSeconds = r.StartSeconds,
            EndSeconds = r.EndSeconds,
            Include = r.Include,
            IsTemporary = r.IsTemporary,
            Preset = r.Preset,
            Cq = r.Cq,
        };
        HookChapter(chapter);
        return chapter;
    }

    /// <summary>Botão "Resetar Capítulos": só existe com grade editada salva para o vídeo.</summary>
    private void UpdateResetButton() =>
        BtnResetChapters.Visibility = _selected?.HasChapterEdits == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Grava a grade atual como edição do episódio (chamado ao adicionar/editar/
    /// remover capítulo). Grade vazia apaga a edição — volta ao comportamento original.</summary>
    private void SaveChapterEdits(EpisodeViewModel? ep)
    {
        if (ep is null)
            return;

        try
        {
            if (ep.Chapters.Count == 0)
            {
                AppServices.ChapterEdits.Delete(ep.FullPath);
                ep.HasChapterEdits = false;
            }
            else
            {
                AppServices.ChapterEdits.Save(ep.FullPath, [.. ep.Chapters.Select(c =>
                    new ChapterEditRecord(c.Number, c.Title, c.StartSeconds, c.EndSeconds,
                        ChapterEditsStore.FormatClass(c.Class), c.TargetKbps, c.Include, c.IsTemporary,
                        c.Preset, c.Cq))]);
                ep.HasChapterEdits = true;
            }
            UpdateResetButton();
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, ep.FileName, ex.Message);
        }
    }

    private void BtnResetChapters_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } ep)
            return;

        try
        {
            AppServices.ChapterEdits.Delete(ep.FullPath);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, ep.FileName, ex.Message);
            return;
        }

        ep.HasChapterEdits = false;
        UpdateResetButton();

        // reconstrói a grade a partir do vídeo (probe em cache) com bitrates da série —
        // exatamente o estado de quem nunca editou este episódio
        if (ep.Probed is not { } info)
            return;

        Ui.Safe(async () =>
        {
            var keywords = await AppServices.Keywords.GetAllAsync();
            ep.Chapters.Clear();
            var ranges = AppServices.Chapters.BuildRanges(info.Chapters, info.DurationSeconds);
            foreach (var r in ranges)
                ep.Chapters.Add(CreateChapterVM(r.Number, r.Title, r.StartSeconds, r.EndSeconds, ep.Series, keywords));
            ResequenceChapters();
            await UpdateSizeEstimateAsync();
        }, ex => ShowStatus(InfoBarSeverity.Error, ep.FileName, ex.Message));
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
            c.TargetLabel = BuildTargetLabel(c.Class, c.TargetKbps, c.Cq, cfg);

        await UpdateSizeEstimateAsync();
    }

    /// <summary>Rótulo do alvo do capítulo: em CQ mostra o CQ (o do capítulo quando tem
    /// override); em bitrate médio mostra os kbps da parte.</summary>
    private string BuildTargetLabel(BitrateClass cls, int kbps, int cq, CodecEncodeConfig cfg) =>
        cfg.UseConstantQuality
            ? cq > 0 ? $"{ClassLabel(cls)} · CQ {cq}" : ClassLabel(cls)
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
        Ui.ModelFromIndex(CmbUpscaleModel.SelectedIndex),
        Ui.HeightFromResolutionIndex(CmbUpscaleResolution.SelectedIndex),
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
            // a remoção também persiste: sem isso o capítulo removido ressuscitaria
            // ao reabrir o episódio (a grade salva vence os capítulos do vídeo)
            RecomputeChapterEnds(_selected);
            SaveChapterEdits(_selected);
        }
    }

    /// <summary>Duração de referência da grade: a da sonda; sem sonda, o maior fim já conhecido
    /// (fallback raro — a sonda roda ao selecionar o episódio).</summary>
    private double EffectiveDuration(EpisodeViewModel ep) =>
        ep.Probed is { } info && info.DurationSeconds > 0
            ? info.DurationSeconds
            : Chapters().LastOrDefault()?.EndSeconds ?? 0;

    /// <summary>Recalcula o FIM de todos os capítulos da grade: o usuário só informa o início —
    /// o final é o início do próximo (na linha do tempo) e o do último é a duração do vídeo.</summary>
    private void RecomputeChapterEnds(EpisodeViewModel? ep)
    {
        if (ep is null)
            return;
        var chapters = Chapters().ToList();
        if (chapters.Count == 0)
            return;

        var duration = EffectiveDuration(ep);
        var ends = ChapterTimeline.DeriveEnds([.. chapters.Select(c => c.StartSeconds)], duration);
        for (var i = 0; i < chapters.Count; i++)
        {
            chapters[i].EndSeconds = ends[i];
            chapters[i].TimeRange = $"{FormatTime(chapters[i].StartSeconds)} → {FormatTime(ends[i])}";
        }
    }

    /// <summary>Opções de preset do modal: 0 = "usa o da configuração"; depois 1..N na faixa
    /// do codec selecionado (NVENC p1–p7; SVT/Av1an 1–13).</summary>
    private (List<string> Labels, int MaxPreset) PresetOptions(CodecEncodeConfig cfg)
    {
        var max = cfg.Code.StartsWith("nvenc", StringComparison.Ordinal) ? 7 : 13;
        var labels = new List<string> { AppServices.Localizer.T("episodes.addDialog.presetDefault") };
        for (var i = 1; i <= max; i++)
            labels.Add(i.ToString());
        return (labels, max);
    }

    private async void EditChapter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChapterItemViewModel item } ||
            ChaptersList.ItemsSource is not ObservableCollection<ChapterItemViewModel> col)
            return;

        var t = AppServices.Localizer;
        var cfg = await GetSelectedCodecConfigAsync();
        var title = new TextBox { Header = t.T("episodes.addDialog.name"), Text = item.Title };
        var start = new TextBox { Header = t.T("episodes.addDialog.start"), Text = FormatTime(item.StartSeconds) };
        var (presetLabels, presetMax) = PresetOptions(cfg);
        var preset = new ComboBox
        {
            Header = t.T("episodes.addDialog.preset"),
            MinWidth = 220,
            ItemsSource = presetLabels,
            SelectedIndex = item.Preset > 0 ? Math.Min(item.Preset, presetMax) : 0,
        };
        // CQ configurado no codec → o campo do capítulo é o Quality; senão, o bitrate alvo
        var quality = cfg.UseConstantQuality
            ? null
            : new NumberBox
            {
                Header = t.T("episodes.addDialog.kbps"),
                Value = item.TargetKbps,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = 50,
                Minimum = 0,
            };
        var cq = cfg.UseConstantQuality
            ? new NumberBox
            {
                Header = t.T("episodes.addDialog.quality"),
                Value = item.Cq > 0 ? item.Cq : cfg.Cq,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = 1,
                Minimum = 0,
                Maximum = 63,
            }
            : null;
        var temporary = new CheckBox
        {
            Content = t.T("episodes.addDialog.temporary"),
            IsChecked = item.IsTemporary,
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
        panel.Children.Add(preset);
        if (quality is not null)
            panel.Children.Add(quality);
        if (cq is not null)
            panel.Children.Add(cq);
        panel.Children.Add(temporary);
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
            var duration = EffectiveDuration(_selected!);
            if (!TryParseTime(start.Text, out var startSeconds) ||
                startSeconds < 0 || startSeconds >= duration ||
                Chapters().Any(c => c != item && Math.Abs(c.StartSeconds - startSeconds) < 0.001))
            {
                args.Cancel = true;
                error.Text = t.T("episodes.addDialog.invalidStart");
                error.Visibility = Visibility.Visible;
                return;
            }

            var presetValue = preset.SelectedIndex > 0 ? preset.SelectedIndex : 0;
            var kbpsValue = quality is { } q && !double.IsNaN(q.Value) ? (int)q.Value : item.TargetKbps;
            var cqValue = cq is { } c && !double.IsNaN(c.Value) ? (int)c.Value : 0;
            var newTitle = string.IsNullOrWhiteSpace(title.Text) ? item.Title : title.Text.Trim();

            await Task.Yield(); // deixa o dialog fechar antes de mexer na coleção

            // substitui e REORDENA pelo novo início (a ordem da grade é a linha do tempo);
            // os fins de toda a grade são derivados de novo
            var index = col.IndexOf(item);
            if (index < 0)
                return;

            var updated = new ChapterItemViewModel
            {
                Number = item.Number,
                Title = newTitle,
                TimeRange = $"{FormatTime(startSeconds)} → …",
                Class = item.Class,
                TargetKbps = kbpsValue,
                TargetLabel = BuildTargetLabel(item.Class, kbpsValue, cqValue, await GetSelectedCodecConfigAsync()),
                StartSeconds = startSeconds,
                EndSeconds = duration,
                Include = item.Include,
                IsTemporary = temporary.IsChecked == true,
                Preset = presetValue,
                Cq = cqValue,
            };
            HookChapter(updated);
            col.Remove(item);
            var insertAt = col.Count;
            for (var i = 0; i < col.Count; i++)
            {
                if (col[i].StartSeconds > startSeconds)
                {
                    insertAt = i;
                    break;
                }
            }
            col.Insert(insertAt, updated);
            RecomputeChapterEnds(_selected);
            SaveChapterEdits(_selected);
        };

        _ = dialog.ShowAsync();
    }

    private async void BtnAddChapter_Click(object sender, RoutedEventArgs e)
    {
        if (ChaptersList.ItemsSource is not ObservableCollection<ChapterItemViewModel>)
            return;

        var t = AppServices.Localizer;
        var cfg = await GetSelectedCodecConfigAsync();
        var query = new TextBox { PlaceholderText = t.T("episodes.newChapter"), Text = t.T("episodes.newChapter") };
        var start = new TextBox { PlaceholderText = "0:00.000" };
        var last = Chapters().LastOrDefault();
        if (last is not null)
            start.Text = FormatTime(last.StartSeconds);
        var (presetLabels, presetMax) = PresetOptions(cfg);
        var preset = new ComboBox
        {
            Header = t.T("episodes.addDialog.preset"),
            MinWidth = 220,
            ItemsSource = presetLabels,
            SelectedIndex = 0,
        };
        var kbps = cfg.UseConstantQuality
            ? null
            : new NumberBox
            {
                Value = _selected?.Series?.EpisodeKbps ?? DefaultEpisodeKbps,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = 50,
                Minimum = 0,
            };
        var cq = cfg.UseConstantQuality
            ? new NumberBox
            {
                Header = t.T("episodes.addDialog.quality"),
                Value = cfg.Cq,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = 1,
                Minimum = 0,
                Maximum = 63,
            }
            : null;
        if (kbps is not null)
            kbps.Header = t.T("episodes.addDialog.kbps");
        var temporary = new CheckBox { Content = t.T("episodes.addDialog.temporary") };
        var temporaryHint = new TextBlock
        {
            Text = t.T("episodes.addDialog.temporaryHint"),
            FontSize = 12,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
        };
        var error = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(query);
        panel.Children.Add(start);
        panel.Children.Add(preset);
        if (kbps is not null)
            panel.Children.Add(kbps);
        if (cq is not null)
            panel.Children.Add(cq);
        panel.Children.Add(temporary);
        panel.Children.Add(temporaryHint);
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
            // SÓ tempo inicial: o final vira o início do próximo capítulo (último = fim do vídeo)
            var duration = EffectiveDuration(_selected!);
            if (!TryParseTime(start.Text, out var startSeconds) ||
                startSeconds < 0 || startSeconds >= duration ||
                Chapters().Any(c => Math.Abs(c.StartSeconds - startSeconds) < 0.001))
            {
                args.Cancel = true;
                error.Text = t.T("episodes.addDialog.invalidStart");
                error.Visibility = Visibility.Visible;
                return;
            }

            var presetValue = preset.SelectedIndex > 0 ? preset.SelectedIndex : 0;
            var kbpsValue = kbps is { } k && !double.IsNaN(k.Value) ? (int)k.Value : DefaultEpisodeKbps;
            var cqValue = cq is { } c && !double.IsNaN(c.Value) ? (int)c.Value : 0;
            var chapter = new ChapterItemViewModel
            {
                Title = string.IsNullOrWhiteSpace(query.Text) ? t.T("episodes.newChapter") : query.Text.Trim(),
                TimeRange = $"{FormatTime(startSeconds)} → …",
                Class = BitrateClass.Episode,
                TargetKbps = kbpsValue,
                TargetLabel = BuildTargetLabel(BitrateClass.Episode, kbpsValue, cqValue, await GetSelectedCodecConfigAsync()),
                StartSeconds = startSeconds,
                EndSeconds = duration,
                IsTemporary = temporary.IsChecked == true,
                Preset = presetValue,
                Cq = cqValue,
            };
            HookChapter(chapter);

            await Task.Yield(); // deixa o dialog fechar antes de mexer na coleção
            if (ChaptersList.ItemsSource is ObservableCollection<ChapterItemViewModel> col)
            {
                // entra na posição cronológica — a ordem da grade é a linha do tempo
                var insertAt = col.Count;
                for (var i = 0; i < col.Count; i++)
                {
                    if (col[i].StartSeconds > startSeconds)
                    {
                        insertAt = i;
                        break;
                    }
                }
                col.Insert(insertAt, chapter);
                RecomputeChapterEnds(_selected);
                // adicionar/editar capítulo cria a grade salva do episódio (chapters-edits)
                SaveChapterEdits(_selected);
            }
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
        SizeVideoLine.Text = t.T("episodes.sizeVideo", videoBytes > 0 ? Ui.FormatSize((long)videoBytes) : "—");
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
        SizeVideoLine.Text = t.T("episodes.sizeVideo", cqMode ? "—" : Ui.FormatSize((long)(videoKb * 1024)));
        SizeAudioLine.Text = t.T("episodes.sizeAudio", Ui.FormatSize((long)(audioKb * 1024)));
        SizeTotalLine.Text = cqMode ? "—" : t.T("episodes.sizeTotal", Ui.FormatSize((long)((videoKb + audioKb) * 1024)));
    }

    // ---------- enfileirar ----------

    private void BtnEnqueue_Click(object sender, RoutedEventArgs e) =>
        Ui.Safe(EnqueueCheckedAsync, ex => ShowStatus(InfoBarSeverity.Error,
            AppServices.Localizer.T("episodes.enqueue"), ex.Message));

    /// <summary>Probe serial dos episódios marcados + gravação dos jobs no banco.</summary>
    private async Task EnqueueCheckedAsync()
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
        var totalReused = 0;

        foreach (var ep in checkedEpisodes)
        {
            await ProbeEpisodeAsync(ep);
            if (ep.Probed is null)
                continue; // falhou o probe — pula e segue

            // Ordem de exibição da lista (com edições do usuário) é a ordem das partes;
            // capítulos originais do arquivo NÃO vão pro arquivo gerado.
            if (ep.Chapters.Count == 0)
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

            // Order = número do capítulo NA GRADE (c.Number, renumerado a cada edição) — não a
            // sequência dos marcados: a parte sai "02 - Intro…" igual ao capítulo de origem e
            // dois jobs parciais do mesmo episódio não sobrescrevem o "01" um do outro.
            //
            // Capítulos MARCADOS viram partes a encodear. Capítulos DESMARCADOS cuja parte já
            // existe na pasta de trabalho são ADOTADOS como Done — o merge final junta tudo
            // (comportamento do script original), e converter um capítulo isolado não gera
            // mais um arquivo final só com aquele pedaço.
            var baseName = Path.GetFileNameWithoutExtension(ep.FullPath);
            var reused = 0;
            foreach (var c in ep.Chapters)
            {
                var partPath = JobPaths.PartPath(AppServices.GetOutputDirectory(), baseName, c.Number, c.Title);
                var alreadyEncoded = !c.Include && File.Exists(partPath);
                if (!c.Include && !alreadyEncoded)
                    continue; // não marcado e sem parte pronta — não entra no job

                if (alreadyEncoded)
                    reused++;

                job.Items.Add(new JobItem
                {
                    Order = c.Number,
                    State = alreadyEncoded ? JobItemState.Done : JobItemState.Pending,
                    OutputPath = alreadyEncoded ? partPath : null,
                    Title = c.Title,
                    StartSeconds = c.StartSeconds,
                    EndSeconds = c.EndSeconds,
                    Class = c.Class,
                    TargetKbps = c.TargetKbps,
                    IsTemporary = c.IsTemporary,
                    Preset = c.Preset > 0 ? c.Preset : null,
                    Cq = c.Cq > 0 ? c.Cq : null,
                });
            }

            if (job.Items.Count == 0)
                continue;

            await AppServices.Jobs.AddAsync(job);
            enqueued++;
            totalParts += job.Items.Count;
            totalReused += reused;
        }

        BtnEnqueue.IsEnabled = true;
        var t = AppServices.Localizer;
        var upscaleInfo = mode == UpscaleMode.None ? "" : t.T("episodes.upscaleInfo", model, height);
        EnqueueStatus.Text = enqueued == 0
            ? t.T("episodes.nothingEnqueued")
            : t.T("episodes.enqueued", enqueued, totalParts, upscaleInfo) +
              (registered > 0 ? t.T("episodes.newSeries", registered) : "") +
              (totalReused > 0 ? t.T("episodes.reused", totalReused) : "");

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
