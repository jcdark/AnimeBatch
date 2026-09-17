using AnimeBatch.App.Services;
using AnimeBatch.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeBatch.App.Views;

/// <summary>Linha da lista de placas: nome + VRAM + seletor de workers (0, 1 ou 2).</summary>
public partial class GpuRowViewModel : ObservableObject
{
    public HardwareGpuCard Card { get; }

    public string NameLabel => Card.Name;

    public string MemoryLabel => Card.MemoryBytes > 0
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.#} GB", Card.MemoryBytes / (1024.0 * 1024 * 1024))
        : "";

    public IReadOnlyList<string> WorkerOptions { get; }

    /// <summary>O índice do combo É o número de workers (0 = placa excluída; TwoWay no combo).</summary>
    [ObservableProperty]
    private int workersIndex;

    public GpuRowViewModel(HardwareGpuCard card, IReadOnlyList<string> workerOptions)
    {
        Card = card;
        WorkerOptions = workerOptions;
        workersIndex = Math.Clamp(card.Workers, 0, workerOptions.Count - 1);
    }

    public HardwareGpuCard ToCard() => Card with { Workers = WorkersIndex };
}

/// <summary>
/// Hardware (GPU): lista as placas DEDICADAS (PCI) do computador — integradas na CPU ficam
/// fora — e deixa o usuário escolher 0, 1 ou 2 workers por placa (0 = nada vai para ela).
/// A configuração é guardada por NOME (setting "hardware.gpus") e o QueueRunner resolve os
/// índices certos para cada motor (Vulkan no ncnn, DirectML no ONNX, NVENC no encode).
/// Vale a partir do próximo job da fila.
/// </summary>
public sealed partial class HardwarePage : Page
{
    private List<GpuRowViewModel> _rows = [];

    public HardwarePage()
    {
        InitializeComponent();
        Localize();
        _ = LoadAsync();
    }

    private void Localize()
    {
        var t = AppServices.Localizer;
        TitleText.Text = t.T("hardware.title");
        HintText.Text = t.T("hardware.hint");
        BtnSave.Content = t.T("hardware.save");
        BtnAuto.Content = t.T("hardware.auto");
        NoneText.Text = t.T("hardware.none");
    }

    private static IReadOnlyList<DxgiAdapter> DetectCards()
    {
        var adapters = DxgiGpuProbe.Enumerate();
        if (adapters.Count == 0)
            adapters = DxgiGpuProbe.AdapterNamesFallback();
        return [.. adapters.Where(a => !HardwareGpuService.IsIntegrated(a.Name))];
    }

    private async Task LoadAsync()
    {
        var t = AppServices.Localizer;
        var cards = DetectCards();
        var saved = HardwareGpuService.Parse(
            await AppServices.Settings.GetAsync(HardwareGpuService.SettingKey).ConfigureAwait(true));

        var options = new List<string>
        {
            t.T("hardware.workers0") ?? "0 workers",
            t.T("hardware.workers1") ?? "1 worker",
            t.T("hardware.workers2") ?? "2 workers",
        };
        _rows = [.. cards.Select(a =>
        {
            var workers = saved.FirstOrDefault(c =>
                HardwareGpuService.NormalizeName(c.Name) == HardwareGpuService.NormalizeName(a.Name))?.Workers ?? 1;
            return new GpuRowViewModel(
                new HardwareGpuCard(a.Name, workers, a.DedicatedVideoMemoryBytes), options);
        })];

        CardsList.ItemsSource = _rows;
        NoneText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardsList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = "";
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var cards = _rows.Select(r => r.ToCard()).ToList();
        await AppServices.Settings
            .SetAsync(HardwareGpuService.SettingKey, HardwareGpuService.Serialize(cards))
            .ConfigureAwait(true);
        StatusText.Text = AppServices.Localizer.T("hardware.saved");
    }

    private async void BtnAuto_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.Settings
            .SetAsync(HardwareGpuService.SettingKey, "")
            .ConfigureAwait(true);
        // índice 1 = 1 worker (o índice do combo é o número de workers)
        foreach (var row in _rows)
            row.WorkersIndex = 1;
        StatusText.Text = AppServices.Localizer.T("hardware.autoRestored");
    }
}
