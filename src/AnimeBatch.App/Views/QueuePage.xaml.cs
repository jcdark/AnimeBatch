using AnimeBatch.App.Services;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace AnimeBatch.App.Views;

public partial class JobRowViewModel : ObservableObject
{
    public Job Job { get; private set; }

    public JobRowViewModel(Job job)
    {
        Job = job;
        Order = job.Order;
    }

    [ObservableProperty]
    private int order;

    /// <summary>Percentual vivo do job em execução; null nos demais estados.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private double? progressPercent;

    public int Id => Job.Id;
    public string SourceFileName => System.IO.Path.GetFileName(Job.SourcePath);

    /// <summary>Motivo do erro (visível na row quando o job está em Error).</summary>
    public string ErrorLabel => Job.ErrorMessage ?? "";

    public Visibility ErrorVisibility =>
        string.IsNullOrEmpty(Job.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    public string StateLabel => Job.State == JobState.Running && ProgressPercent is { } p
        ? $"{Job.State} · {p:0}%"
        : Job.State.ToString();

    public string ItemsSummary =>
        AppServices.Localizer.T("queue.parts", Job.Items.Count(i => i.State != JobItemState.Skipped));

    public string CodecLabel =>
        VideoCodecOptions.Options.FirstOrDefault(o => o.Code == Job.VideoCodec).Label
        ?? Job.VideoCodec ?? "—";

    public string UpscaleLabel => Job.UpscaleMode switch
    {
        UpscaleMode.Only => $"{UpscaleModelName()} → {Job.UpscaleTargetHeight}p · {AppServices.Localizer.T("queue.upscaleOnly")}",
        UpscaleMode.WithEncode => $"{UpscaleModelName()} → {Job.UpscaleTargetHeight}p · {AppServices.Localizer.T("queue.upscaleBoth")}",
        _ => AppServices.Localizer.T("queue.upscaleNone"),
    };

    private string UpscaleModelName() =>
        Job.UpscaleModel switch
        {
            "realesrgan" => AppServices.Localizer.T("episodes.modelEsrgan").Split(" (")[0],
            "onnx" => AppServices.Localizer.T("episodes.modelOnnx"),
            _ => AppServices.Localizer.T("episodes.modelCugan"),
        };

    public string CreatedAtLabel => Job.CreatedAt.ToString("dd/MM HH:mm");

    /// <summary>Atualiza a linha IN-PLACE (sem reconstruir a lista — era isso que fazia piscar).</summary>
    public void UpdateFrom(Job job, double? livePercent)
    {
        Job = job;
        Order = job.Order;
        ProgressPercent = job.State == JobState.Running ? livePercent ?? 0 : null;
        OnPropertyChanged(nameof(SourceFileName));
        OnPropertyChanged(nameof(ErrorLabel));
        OnPropertyChanged(nameof(ErrorVisibility));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ItemsSummary));
        OnPropertyChanged(nameof(CodecLabel));
        OnPropertyChanged(nameof(UpscaleLabel));
        OnPropertyChanged(nameof(CreatedAtLabel));
    }
}

public sealed partial class QueuePage : Page
{
    private ObservableCollection<JobRowViewModel>? _rows;
    private readonly Dictionary<int, double> _lastPercent = [];

    public QueuePage()
    {
        InitializeComponent();
        Localize();
        Load();

        AppServices.Queue.Changed += () => DispatcherQueue.TryEnqueue(Load);
        AppServices.Queue.Progress += (jobId, pct) => DispatcherQueue.TryEnqueue(() =>
        {
            _lastPercent[jobId] = pct;
            if (_rows is not null)
            {
                var row = _rows.FirstOrDefault(r => r.Id == jobId);
                row?.ProgressPercent = pct;
            }
        });
        AppServices.Queue.StatsUpdated += _ => { }; // rodapé é tratado na MainWindow

        // Preferência persistida: remover da fila ao ser convertido
        ChkAutoRemove.IsChecked = AppServices.Settings
            .GetAsync(SettingsRepository.QueueAutoRemove)
            .GetAwaiter().GetResult() == "true";
    }

    private async void ChkAutoRemove_Changed(object sender, RoutedEventArgs e)
    {
        await AppServices.Settings.SetAsync(
            SettingsRepository.QueueAutoRemove,
            ChkAutoRemove.IsChecked == true ? "true" : "false");
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        // Remove tudo, exceto o job em execução agora (ele conclui e entra no histórico)
        var removed = AppServices.Jobs.ClearAllAsync().GetAwaiter().GetResult();
        Load();
        Info.Severity = InfoBarSeverity.Success;
        Info.Title = AppServices.Localizer.T("queue.cleared");
        Info.Message = AppServices.Localizer.T("queue.clearedDetail", removed);
        Info.IsOpen = true;
    }

    private void Localize()
    {
        var t = AppServices.Localizer;
        TitleText.Text = t.T("queue.title");
        BtnStart.Content = t.T("queue.start");
        BtnPause.Content = t.T("queue.pause");
        BtnStop.Content = t.T("queue.stop");
        BtnUp.Content = t.T("queue.up");
        BtnDown.Content = t.T("queue.down");
        BtnTop.Content = t.T("queue.top");
        BtnRemove.Content = t.T("queue.remove");
        BtnClear.Content = t.T("queue.clear");
        ChkAutoRemove.Content = t.T("queue.autoRemove");
    }

    /// <summary>
    /// Reconcilia as linhas: só reconstrói a lista quando a ordem/quantidade muda;
    /// no dia a dia atualiza cada linha in-place, sem piscar.
    /// </summary>
    private void Load()
    {
        var jobs = AppServices.Jobs.GetAllOrderedAsync().GetAwaiter().GetResult();
        var ids = jobs.Select(j => j.Id).ToList();

        if (_rows is null || !_rows.Select(r => r.Id).SequenceEqual(ids))
        {
            _rows = new ObservableCollection<JobRowViewModel>(jobs.Select(j => new JobRowViewModel(j)));
            JobsList.ItemsSource = _rows;
        }

        foreach (var job in jobs)
        {
            var row = _rows.First(r => r.Id == job.Id);
            double? pct = job.State == JobState.Running && _lastPercent.TryGetValue(job.Id, out var p) ? p : null;
            row.UpdateFrom(job, pct);
        }

        BtnStart.IsEnabled = !AppServices.Queue.IsRunning;
        BtnPause.IsEnabled = AppServices.Queue.IsRunning;
        BtnStop.IsEnabled = AppServices.Queue.IsRunning;
    }

    private JobRowViewModel? Selected => JobsList.SelectedItem as JobRowViewModel;

    private void MoveSelected(int delta)
    {
        if (Selected?.Job is not { State: JobState.Pending or JobState.Paused } job)
            return;
        AppServices.Jobs.MoveAsync(job.Id, delta).GetAwaiter().GetResult();
        Load();
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void BtnDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);
    private void BtnTop_Click(object sender, RoutedEventArgs e) => MoveSelected(int.MinValue);

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
            return;
        AppServices.Jobs.DeleteAsync(Selected.Job.Id).GetAwaiter().GetResult();
        Load();
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: JobRowViewModel row })
        {
            AppServices.Jobs.DeleteAsync(row.Job.Id).GetAwaiter().GetResult();
            Load();
        }
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var tools = AppServices.Tools;
        if (tools.FfmpegPath is null || tools.MkvMergePath is null || AppServices.Probe is null)
        {
            Info.Severity = InfoBarSeverity.Error;
            Info.Title = AppServices.Localizer.T("episodes.ffprobeMissingTitle");
            Info.Message = AppServices.Localizer.T("episodes.ffprobeMissingMsg");
            Info.IsOpen = true;
            return;
        }

        await AppServices.Queue.RunAsync();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) => AppServices.Queue.Pause();

    private void BtnStop_Click(object sender, RoutedEventArgs e) => AppServices.Queue.Stop();

    private async void EditJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRowViewModel row })
            return;
        if (AppServices.Queue.IsRunning)
            return; // não editar com a fila processando

        var t = AppServices.Localizer;
        var job = row.Job;

        var cmbCodec = new ComboBox
        {
            Header = t.T("episodes.codec"),
            MinWidth = 220,
            ItemsSource = VideoCodecOptions.Options.Select(o => o.Label).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(VideoCodecOptions.Options, o => o.Code == (job.VideoCodec ?? "nvenc_av1_10bit"))),
        };
        var cmbMode = new ComboBox
        {
            Header = t.T("episodes.upscaleMode"),
            MinWidth = 220,
            SelectedIndex = (int)job.UpscaleMode,
        };
        foreach (var label in new[]
                 {
                     t.T("episodes.upscaleNone"), t.T("episodes.upscaleOnly"), t.T("episodes.upscaleBoth"),
                 })
            cmbMode.Items.Add(label);

        var cmbModel = new ComboBox
        {
            Header = t.T("episodes.model"),
            MinWidth = 220,
            SelectedIndex = job.UpscaleModel switch
            {
                "realesrgan" => 1,
                "onnx" => 2,
                _ => 0,
            },
        };
        cmbModel.Items.Add(t.T("episodes.modelCugan"));
        cmbModel.Items.Add(t.T("episodes.modelEsrgan"));
        cmbModel.Items.Add(t.T("episodes.modelOnnx"));

        var cmbRes = new ComboBox
        {
            Header = t.T("episodes.resolution"),
            MinWidth = 220,
            SelectedIndex = job.UpscaleTargetHeight switch
            {
                720 => 0,
                1440 => 2,
                2160 => 3,
                _ => 1,
            },
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(cmbCodec);
        panel.Children.Add(cmbMode);
        panel.Children.Add(cmbModel);
        panel.Children.Add(cmbRes);

        var dialog = new ContentDialog
        {
            Title = t.T("queue.editTitle"),
            Content = panel,
            CloseButtonText = t.T("episodes.addDialog.cancel"),
            PrimaryButtonText = t.T("series.save"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var updated = new Job
            {
                Id = job.Id,
                SourcePath = job.SourcePath,
                SeriesName = job.SeriesName,
                EpisodeKbps = job.EpisodeKbps,
                OpeningKbps = job.OpeningKbps,
                EndingKbps = job.EndingKbps,
                UpscaleMode = (UpscaleMode)Math.Max(0, cmbMode.SelectedIndex),
                UpscaleModel = cmbMode.SelectedIndex == 0
                    ? null
                    : cmbModel.SelectedIndex switch { 1 => "realesrgan", 2 => "onnx", _ => "realcugan" },
                UpscaleTargetHeight = cmbMode.SelectedIndex == 0 ? null : cmbRes.SelectedIndex switch
                {
                    0 => 720,
                    2 => 1440,
                    3 => 2160,
                    _ => 1080,
                },
                VideoCodec = VideoCodecOptions.Options[Math.Clamp(cmbCodec.SelectedIndex, 0, VideoCodecOptions.Options.Length - 1)].Code,
            };

            await AppServices.Jobs.UpdateAsync(updated);
            await Task.Yield();
            Load();
        };

        await dialog.ShowAsync();
    }
}
