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

        ApplyAppIcon();
        UpdateFooter();
    }

    /// <summary>Logo: ícone da janela/barra de tarefas (app desempacotado precisa setar em
    /// runtime) + imagem no cabeçalho do menu lateral. Falha de ícone não derruba o boot.</summary>
    private void ApplyAppIcon()
    {
        try
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(icoPath))
                AppWindow.SetIcon(icoPath);

            var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (System.IO.File.Exists(pngPath))
                LogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new System.Uri(pngPath));
        }
        catch
        {
            // sem ícone/logo o app continua funcionando normalmente
        }
    }

    /// <summary>Ex.: "AnimeBatch (BETA) V0.46" — versão vem do csproj (via AssemblyName/Version).</summary>
    public static string AppTitle()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var v = asm?.GetName().Version;
        var name = asm?.GetName().Name ?? "AnimeBatch"; // AnimeBatchV0.22
        var display = name.StartsWith("AnimeBatchV", StringComparison.Ordinal) ? name["AnimeBatchV".Length..] : "";
        return $"AnimeBatch (BETA) V{(display.Length > 0 ? display : v is null ? "?" : $"{v.Major}.{v.Minor}")}";
    }

    // O logo do AB no topo do painel substitui o hamburger ENQUANTO O PAINEL ESTÁ ABERTO
    // (clicar nele fecha). O PaneHeader do NavigationView não existe com o painel fechado —
    // então no fechado o hamburger de fábrica volta, garantindo que sempre haja como reabrir.
    private void PaneToggle_Click(object sender, RoutedEventArgs e) => Nav.IsPaneOpen = false;

    private void Nav_PaneOpened(NavigationView sender, object args)
    {
        Nav.IsPaneToggleButtonVisible = false; // aberto: logo + nome no comando
    }

    private void Nav_PaneClosed(NavigationView sender, object args)
    {
        Nav.IsPaneToggleButtonVisible = true; // fechado: hamburger de fábrica reabre o painel
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

        // Encode multipass (SVT 2-pass): qual passada está rodando agora
        if (s.EncodePass > 0)
            parts.Add(t.T("footer.pass", s.EncodePass));

        // Bitrate alvo (ou CQ) do encode em curso (sequencial — no paralelo o rate é por worker)
        if (!string.IsNullOrEmpty(s.Rate))
            parts.Add(t.T("footer.rate", s.Rate));

        // Motor Av1an: fase do pipeline ANTES do primeiro chunk + contador de chunks.
        // A análise de cenas é single-core e silenciosa por MINUTOS — e o "Queue N Workers"
        // sai do log logo no boot, então sem o rótulo da fase o "chunk 0/153" com FPS 0.0
        // parecia encode travado (o dono leu como "1º passe sem informação"). Não existe
        // "passo 1/2" nesse motor: as passadas rodam DENTRO de cada chunk.
        if (s.Phase is "scenes" or "preparing" or "segmenting")
            parts.Add(t.T($"footer.phase.{s.Phase}"));
        if (s.ChunksTotal > 0)
            parts.Add(t.T("footer.chunks", s.ChunksDone, s.ChunksTotal));

        parts.AddRange(
        [
            t.T("footer.fps", s.Fps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
            t.T("footer.speed", s.Speed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
            t.T("footer.elapsed", FormatClock(s.Elapsed)),
            s.Remaining is { } r ? t.T("footer.remaining", FormatClock(r)) : t.T("footer.remainingUnknown"),
            t.T("footer.converted", s.ConvertedMinutes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
        ]);

        // Encode PARALELO: uma linha por worker ativo (parte + rate/fps/velocidade próprios),
        // embaixo da linha de totais — o rodapé cresce, e pode.
        if (s.Workers is { Length: > 0 })
        {
            var lines = new List<string> { string.Join("   ·   ", parts) };
            foreach (var w in s.Workers)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                lines.Add(w.ChunksTotal > 0
                    ? t.T("footer.workerChunks", w.Title, w.Rate, w.ChunksDone, w.ChunksTotal,
                        w.Fps.ToString("0.0", inv), w.Speed.ToString("0.00", inv))
                    : t.T("footer.worker", w.Title, w.Rate,
                        w.Fps.ToString("0.0", inv), w.Speed.ToString("0.00", inv)));
            }
            FooterText.Text = string.Join("\n", lines);
            return;
        }

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
