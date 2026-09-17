using AnimeBatch.App.Services;
using AnimeBatch.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeBatch.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = AppTitle();
        Localize();
        ContentFrame.Navigate(typeof(QueuePage));
        Nav.SelectedItem = Nav.MenuItems[0];

        AppServices.Queue.StatsUpdated += stats => DispatcherQueue.TryEnqueue(UpdateFooter);
        AppServices.Queue.Changed += () => DispatcherQueue.TryEnqueue(UpdateFooter);
        UpdateFooter();
    }

    /// <summary>Ex.: "AnimeBatch V0.22" — versão vem do csproj (via AssemblyName/Version).</summary>
    public static string AppTitle()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var v = asm?.GetName().Version;
        var name = asm?.GetName().Name ?? "AnimeBatch"; // AnimeBatchV0.22
        var display = name.StartsWith("AnimeBatchV", StringComparison.Ordinal) ? name["AnimeBatchV".Length..] : "";
        return $"AnimeBatch V{(display.Length > 0 ? display : v is null ? "?" : $"{v.Major}.{v.Minor}")}";
    }

    private void UpdateFooter()
    {
        var t = AppServices.Localizer;
        var queue = AppServices.Queue;

        if (!queue.IsRunning)
        {
            FooterText.Text = t.T("footer.ready");
            return;
        }

        var s = queue.LastStats;
        if (s is null)
        {
            FooterText.Text = t.T("footer.starting");
            return;
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(s.CurrentLabel))
            parts.Add(s.CurrentLabel);

        parts.AddRange(
        [
            t.T("footer.fps", s.Fps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
            t.T("footer.speed", s.Speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
            t.T("footer.elapsed", FormatClock(s.Elapsed)),
            s.Remaining is { } r ? t.T("footer.remaining", FormatClock(r)) : t.T("footer.remainingUnknown"),
            t.T("footer.converted", s.ConvertedMinutes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
        ]);

        FooterText.Text = string.Join("   ·   ", parts);
    }

    private static string FormatClock(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");

    private void Localize()
    {
        var t = AppServices.Localizer;
        NavQueue.Content = t.T("nav.queue");
        NavEpisodes.Content = t.T("nav.episodes");
        NavEncodes.Content = t.T("nav.encodes");
        NavHardware.Content = t.T("nav.hardware");
        NavSeries.Content = t.T("nav.series");
        NavSettings.Content = t.T("nav.settings");
        UpdateFooter();
    }

    /// <summary>Chamado quando o idioma muda nas Configurações.</summary>
    public void RefreshLanguage() => Localize();

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        var page = item.Tag switch
        {
            "queue" => typeof(QueuePage),
            "episodes" => typeof(EpisodesPage),
            "encodes" => typeof(EncodesPage),
            "hardware" => typeof(HardwarePage),
            "series" => typeof(SeriesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(QueuePage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }
}
