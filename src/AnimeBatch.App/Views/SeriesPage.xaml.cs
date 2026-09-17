using AnimeBatch.App.Services;
using AnimeBatch.App.ViewModels;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace AnimeBatch.App.Views;

/// <summary>Linha da tabela de histórico de conversões.</summary>
public partial class ConversionRowViewModel : ObservableObject
{
    private readonly ConversionRecord _record;

    public ConversionRowViewModel(ConversionRecord record)
    {
        _record = record;
    }

    public string Data => _record.ConvertedAt.ToString("dd/MM/yyyy HH:mm");
    public string FileName => _record.FileName;
    public string Duracao => TimeSpan.FromSeconds(_record.DurationSeconds).ToString(@"hh\:mm\:ss");
    public string Tamanho => FormatSize(_record.SizeBytes);

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>Item do resultado de busca do TMDB, com miniatura pronta pra exibição.</summary>
public record TmdbResultRow(TmdbSearchResult Result)
{
    public Microsoft.UI.Xaml.Media.ImageSource? Thumb =>
        string.IsNullOrEmpty(Result.PosterPath)
            ? null
            : new BitmapImage(new Uri(TmdbService.PosterUrl(Result.PosterPath, "w154")));

    public string Label =>
        string.IsNullOrEmpty(Result.FirstAirYear) ? Result.Name : $"{Result.Name} ({Result.FirstAirYear})";
}

public sealed partial class SeriesPage : Page
{
    private List<Series> _series = [];
    private bool _creating;
    private bool _suppressComboEvents;

    public SeriesPage()
    {
        InitializeComponent();
        Localize();
        Load();
    }

    private void Localize()
    {
        var t = AppServices.Localizer;
        TitleText.Text = t.T("series.title");
        BtnNew.Content = t.T("series.new");
        BtnSave.Content = t.T("series.save");
        BtnDelete.Content = t.T("series.delete");
        BtnTmdb.Content = t.T("series.searchTmdb");
        EditorName.Header = t.T("series.name");
        EditorEpisode.Header = t.T("series.episodeKbps");
        EditorOpening.Header = t.T("series.openingKbps");
        EditorEnding.Header = t.T("series.endingKbps");
        HistoryTitle.Text = t.T("series.history.title");
        ColDate.Text = t.T("series.history.colDate");
        ColFile.Text = t.T("series.history.colFile");
        ColDuration.Text = t.T("series.history.colDuration");
        ColSize.Text = t.T("series.history.colSize");
        NoImageLabel.Text = t.T("series.noImage");
    }

    // ---------- carregamento ----------

    private void Load()
    {
        _series = AppServices.Series.GetAllAsync().GetAwaiter().GetResult();

        _suppressComboEvents = true;
        var items = new List<string> { AppServices.Localizer.T("series.selectorNew") };
        items.AddRange(_series.Select(s => s.Name));
        SeriesCombo.ItemsSource = items;
        SeriesCombo.SelectedIndex = _series.Count > 0 ? 1 : 0;
        _suppressComboEvents = false;

        LoadSelection();
    }

    private Series? Current =>
        SeriesCombo.SelectedIndex > 0 && SeriesCombo.SelectedIndex - 1 < _series.Count
            ? _series[SeriesCombo.SelectedIndex - 1]
            : null;

    private void LoadSelection()
    {
        var current = Current;
        _creating = current is null;
        FillEditor(current);
        _ = LoadPosterAsync(current);
        LoadOverview(current);
        LoadHistory(current);
    }

    private void FillEditor(Series? s)
    {
        EditorName.Text = s?.Name ?? "";
        EditorEpisode.Value = s?.EpisodeKbps ?? 500;
        EditorOpening.Value = s?.OpeningKbps ?? 1500;
        EditorEnding.Value = s?.EndingKbps ?? 500;
    }

    private async void SeriesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents)
            return;
        LoadSelection();
        await Task.CompletedTask;
    }

    // ---------- CRUD ----------

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        SeriesCombo.SelectedIndex = 0; // "+ Nova série…"
        FillEditor(null);
        EditorStatus.Text = "";
        EditorName.Focus(FocusState.Programmatic);
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var t = AppServices.Localizer;
        var name = EditorName.Text.Trim();
        if (name.Length == 0)
        {
            EditorStatus.Text = t.T("series.nameRequired");
            return;
        }

        var series = new Series
        {
            Id = !_creating && Current is not null ? Current.Id : 0,
            Name = name,
            EpisodeKbps = (int)EditorEpisode.Value,
            OpeningKbps = (int)EditorOpening.Value,
            EndingKbps = (int)EditorEnding.Value,
        };

        try
        {
            var saved = await AppServices.Series.UpsertAsync(series);
            EditorStatus.Text = t.T("series.saved", saved.Name);
            Load();
            SeriesCombo.SelectedItem = saved.Name;
        }
        catch (Exception ex)
        {
            EditorStatus.Text = $"✗ {ex.Message}";
        }
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } series)
        {
            EditorStatus.Text = AppServices.Localizer.T("series.selectToDelete");
            return;
        }

        await AppServices.Series.DeleteAsync(series.Id);
        EditorStatus.Text = AppServices.Localizer.T("series.deleted", series.Name);
        Load();
    }

    // ---------- TMDB ----------

    private async void BtnTmdb_Click(object sender, RoutedEventArgs e)
    {
        var tmdb = AppServices.GetTmdb();
        if (tmdb is null)
        {
            EditorStatus.Text = AppServices.Localizer.T("series.tmdbNoKey");
            return;
        }

        var nameForSearch = EditorName.Text.Trim();
        if (!_creating && Current is null && nameForSearch.Length == 0)
        {
            EditorStatus.Text = AppServices.Localizer.T("series.tmdbSelectFirst");
            return;
        }

        var dialog = BuildTmdbDialog(tmdb, nameForSearch);
        await dialog.ShowAsync();
    }

    private ContentDialog BuildTmdbDialog(TmdbService tmdb, string prefill)
    {
        var t = AppServices.Localizer;

        var query = new TextBox { PlaceholderText = t.T("series.dialog.placeholder"), Text = prefill, MinWidth = 380 };
        var results = new List<TmdbResultRow>();
        var resultsGrid = new GridView
        {
            Height = 340,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            ItemTemplate = (DataTemplate)Resources["TmdbResultTemplate"],
        };
        var searchButton = new Button { Content = t.T("series.dialog.search") };

        // Campo + botão Buscar na mesma linha, botão alinhado à direita
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(query, 0);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(query);
        searchRow.Children.Add(searchButton);

        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(searchRow);
        panel.Children.Add(resultsGrid);

        var dialog = new ContentDialog
        {
            Title = t.T("series.dialog.title"),
            Content = panel,
            CloseButtonText = t.T("series.dialog.cancel"),
            PrimaryButtonText = t.T("series.dialog.link"),
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900d;

        TmdbSearchResult? selected = null;

        // O modal faz uma busca automática ao abrir; se o usuário editar o campo e buscar
        // de novo antes da primeira resposta chegar, a resposta VELHA chegaria depois e
        // sobrescreveria a lista com o resultado da query antiga. O contador de geração
        // garante que só a busca MAIS RECENTE publica resultado.
        var searchGeneration = 0;

        async Task SearchAsync()
        {
            if (string.IsNullOrWhiteSpace(query.Text))
                return;
            var gen = ++searchGeneration;
            searchButton.IsEnabled = false;
            try
            {
                var found = await tmdb.SearchTvAsync(query.Text.Trim());
                if (gen != searchGeneration)
                    return; // resposta atrasada de uma busca antiga — descarta
                results.Clear();
                results.AddRange(found.Select(r => new TmdbResultRow(r)));
                resultsGrid.ItemsSource = results;
                resultsGrid.SelectedIndex = -1;
                dialog.IsPrimaryButtonEnabled = false;
                if (results.Count == 0)
                    EditorStatus.Text = t.T("series.tmdbNoResults");
            }
            catch (Exception ex)
            {
                if (gen == searchGeneration)
                    EditorStatus.Text = $"✗ {ex.Message}";
            }
            finally
            {
                if (gen == searchGeneration)
                    searchButton.IsEnabled = true;
            }
        }

        searchButton.Click += async (_, _) => await SearchAsync();
        query.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
                _ = SearchAsync();
        };

        resultsGrid.SelectionChanged += (_, _) =>
        {
            selected = resultsGrid.SelectedIndex >= 0 && resultsGrid.SelectedIndex < results.Count
                ? results[resultsGrid.SelectedIndex].Result
                : null;
            dialog.IsPrimaryButtonEnabled = selected is not null;
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (selected is null)
            {
                args.Cancel = true;
                return;
            }

            // "Nova série" aberta: salva a série primeiro pra ter um Id pra vincular.
            if (_creating || Current is null)
            {
                args.Cancel = true;
                EditorStatus.Text = AppServices.Localizer.T("series.tmdbSelectFirst");
                return;
            }

            var series = Current;
            try
            {
                await AppServices.Series.UpdateTmdbAsync(series.Id, selected.Id, selected.PosterPath, selected.Overview);

                // reflete na hora no objeto em cache e na tela (baixa o poster já)
                series.TmdbId = selected.Id;
                series.PosterPath = selected.PosterPath;
                series.Overview = selected.Overview;
                EditorStatus.Text = t.T("series.tmdbLinked", selected.Name, selected.Id);
                await LoadPosterAsync(series);
                LoadOverview(series);
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                EditorStatus.Text = $"✗ {ex.Message}";
                return;
            }
        };

        // Busca automática ao abrir o modal (já vem com o nome da série no campo)
        _ = SearchAsync();

        return dialog;
    }

    private async Task LoadPosterAsync(Series? series)
    {
        PosterImage.Source = null;
        var t = AppServices.Localizer;

        if (series?.TmdbId is null || string.IsNullOrEmpty(series.PosterPath))
        {
            NoImageLabel.Text = t.T("series.noImage");
            NoImageLabel.Visibility = Visibility.Visible;
            return;
        }

        var posterFile = PosterCachePath(series.TmdbId.Value);
        try
        {
            if (!File.Exists(posterFile))
            {
                var tmdb = AppServices.GetTmdb();
                if (tmdb is null)
                {
                    NoImageLabel.Text = t.T("series.noImage");
                    NoImageLabel.Visibility = Visibility.Visible;
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(posterFile)!);
                var bytes = await tmdb.DownloadPosterAsync(series.PosterPath);
                await File.WriteAllBytesAsync(posterFile, bytes);
            }

            NoImageLabel.Visibility = Visibility.Collapsed;
            PosterImage.Source = new BitmapImage(new Uri(posterFile));
        }
        catch (Exception ex)
        {
            NoImageLabel.Text = t.T("series.noImage");
            NoImageLabel.Visibility = Visibility.Visible;
            EditorStatus.Text = $"✗ {ex.Message}";
        }
    }

    private static string PosterCachePath(int tmdbId)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data", "posters");
        return Path.Combine(dir, $"{tmdbId}.jpg");
    }

    private void LoadOverview(Series? series)
    {
        OverviewText.Text = string.IsNullOrWhiteSpace(series?.Overview)
            ? AppServices.Localizer.T("series.noOverview")
            : series.Overview;
    }

    private void LoadHistory(Series? series)
    {
        var records = AppServices.Conversions.GetBySeriesAsync(series?.Id).GetAwaiter().GetResult();
        HistoryList.ItemsSource = records.Select(r => new ConversionRowViewModel(r)).ToList();
        HistoryEmpty.Text = AppServices.Localizer.T("series.history.empty");
        HistoryEmpty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
