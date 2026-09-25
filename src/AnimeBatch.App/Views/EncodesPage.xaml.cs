using AnimeBatch.App.Services;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeBatch.App.Views;

public sealed partial class EncodesPage : Page
{
    // Rótulos por família (valores são os que vão pro encoder — independentes de idioma)
    private static readonly (string Label, string Value)[] TuneNvenc =
    [
        ("None", "none"),
        ("High Quality (hq)", "hq"),
        ("Low Latency (ll)", "ll"),
        ("Ultra Low Latency (ull)", "ull"),
        ("Lossless", "lossless"),
    ];

    private static readonly (string Label, string Value)[] TuneSvt =
    [
        ("None", "none"), ("0 — VQ Delay", "0"), ("1 — Psicovisual", "1"),
    ];

    // Todos os codecs do app são AV1 e a saída é sempre 4:2:0 — só existe Main (0):
    // High (1) é 4:4:4 e Professional (2) é 4:2:2/12-bit; o SVT 4.x rejeita com -22.
    private static readonly (string Label, string Value)[] Profiles =
    [
        ("None", "none"), ("Main (0)", "0"),
    ];

    private static readonly string[] Levels = ["Auto", "2.0", "3.0", "4.0", "4.1", "5.0", "5.1", "6.0"];

    private List<(string Label, string Code)> _codecs = [];
    private CodecEncodeConfig? _current;
    private bool _suppressEvents;

    public EncodesPage()
    {
        InitializeComponent();
        Localize();
        LoadCodecs();
        // Reforço de qualidade não combina com Conversão Rápida (que existe pra ser veloz)
        ChkFast.Checked += (s, e) => UpdateBoostEnabled();
        ChkFast.Unchecked += (s, e) => UpdateBoostEnabled();
    }

    private void Localize()
    {
        var t = AppServices.Localizer;
        TitleText.Text = t.T("encodes.title");
        BtnSave.Content = t.T("series.save");
        NumPreset.Header = t.T("encodes.preset");

        // RadioButtons nasce VAZIO — não pode usar o indexer (foi o crash da V0.11)
        _suppressEvents = true;
        RbMode.Items.Clear();
        RbMode.Items.Add(t.T("encodes.cq"));
        RbMode.Items.Add(t.T("encodes.bitrate"));
        RbMode.SelectedIndex = _current?.UseConstantQuality == true ? 0 : 1;
        _suppressEvents = false;

        CqLabel.Text = CqLabelText((int)SldCq.Value);
        CqEndHigh.Text = t.T("encodes.qEndHigh");
        CqEndLow.Text = t.T("encodes.qEndLow");
        ChkMultipass.Content = t.T("encodes.multipass");
        ChkTurbo.Content = t.T("encodes.turbo");
        ChkFast.Content = t.T("encodes.fastConversion");
        ChkBoost.Content = t.T("encodes.qualityBoost");
        CmbTune.Header = t.T("encodes.tune");
        CmbProfile.Header = t.T("encodes.profile");
        CmbLevel.Header = t.T("encodes.level");
        CmbCodec.Header = t.T("encodes.selectCodec");
        CmbParallel.Header = t.T("encodes.parallel");
        ParallelHint.Text = t.T("encodes.parallelHint");

        // Encodes em paralelo (SVT no CPU): 1/2/3 — o índice É o número de workers
        _suppressEvents = true;
        CmbParallel.ItemsSource = new List<string>
        {
            t.T("encodes.parallel1"),
            t.T("encodes.parallel2"),
            t.T("encodes.parallel3"),
        };
        CmbParallel.SelectedIndex = _current is null ? 0 : _current.EffectiveParallelWorkers - 1;
        _suppressEvents = false;

        // Detecção de cenas (só motor Av1an): o índice É o modo (0/1/2)
        CmbScenecut.Header = t.T("encodes.scenecut");
        CmbScenecut.ItemsSource = new List<string>
        {
            t.T("encodes.scenecutPrecise"),
            t.T("encodes.scenecutFast"),
            t.T("encodes.scenecutMax"),
        };
        CmbScenecut.SelectedIndex = _current is null ? 1 : _current.EffectiveScenecutMode;
    }

    private void LoadCodecs()
    {
        _codecs = VideoCodecOptions.Options.Select(o => (o.Label, o.Code)).ToList();
        CmbCodec.ItemsSource = _codecs.Select(c => c.Label).ToList();
        CmbCodec.SelectedIndex = 3; // AV1 10bits NVENC
    }

    private bool IsNvenc => _current?.Code.StartsWith("nvenc", StringComparison.Ordinal) == true;

    private bool IsAv1an => _current?.Code.StartsWith("av1an", StringComparison.Ordinal) == true;

    private async void CmbCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbCodec.SelectedIndex < 0 || CmbCodec.SelectedIndex >= _codecs.Count)
            return;

        _suppressEvents = true;

        var code = _codecs[CmbCodec.SelectedIndex].Code;
        var cfg = _current = await AppServices.EncodeConfigs.GetAsync(code);
        var t = AppServices.Localizer;

        NumPreset.Header = IsNvenc
            ? $"{t.T("encodes.preset")} (1–7)"
            : $"{t.T("encodes.preset")} (1–13)";
        NumPreset.Value = cfg.Preset;
        NumPreset.Maximum = IsNvenc ? 7 : 13;
        NumPreset.Minimum = 1;

        RbMode.SelectedIndex = cfg.UseConstantQuality ? 0 : 1;
        SldCq.Value = cfg.Cq;
        ChkMultipass.IsChecked = cfg.Multipass;
        ChkTurbo.IsChecked = cfg.TurboFirstPass;
        ChkFast.IsChecked = cfg.FastConversion;
        ChkBoost.IsChecked = cfg.QualityBoost;
        UpdateBoostEnabled();

        var tunes = IsNvenc ? TuneNvenc : TuneSvt;
        CmbTune.ItemsSource = tunes.Select(x => x.Label).ToList();
        CmbTune.SelectedIndex = IndexOfValue(tunes, cfg.Tune);

        CmbProfile.ItemsSource = Profiles.Select(x => x.Label).ToList();
        // config antiga com High/Professional (hoje inválidos no AV1 4:2:0) mostra como Main
        CmbProfile.SelectedIndex = IndexOfValue(Profiles, cfg.Profile is "1" or "2" ? "0" : cfg.Profile);

        CmbLevel.ItemsSource = Levels;
        CmbLevel.SelectedIndex = Math.Max(0, Levels.ToList().IndexOf(cfg.Level));

        // Encodes em paralelo: só codecs SVT (NVENC tem pool próprio por placa na aba Hardware)
        _suppressEvents = true;
        CmbParallel.SelectedIndex = cfg.EffectiveParallelWorkers - 1;
        _suppressEvents = false;

        // Detecção de cenas: índice = modo (0/1/2) da config do codec corrente
        CmbScenecut.SelectedIndex = cfg.EffectiveScenecutMode;

        ApplyModeDependencies();
        _suppressEvents = false;
    }

    private static int IndexOfValue((string Label, string Value)[] options, string value)
    {
        var idx = Array.FindIndex(options, o => o.Value == value);
        return idx >= 0 ? idx : 0;
    }

    private void ApplyModeDependencies()
    {
        if (_current is null)
            return;

        var cqMode = RbMode.SelectedIndex == 0;
        CqPanel.Visibility = cqMode ? Visibility.Visible : Visibility.Collapsed;
        BitrateHint.Visibility = cqMode ? Visibility.Collapsed : Visibility.Visible;

        // Multipass e Turbo só fazem sentido no modo Taxa de Bits Média — somem no modo CQ
        var bitrateMode = RbMode.SelectedIndex == 1;
        ChkMultipass.Visibility = bitrateMode ? Visibility.Visible : Visibility.Collapsed;
        ChkTurbo.Visibility = bitrateMode && !IsNvenc ? Visibility.Visible : Visibility.Collapsed;
        ChkTurbo.IsEnabled = bitrateMode && !IsNvenc && ChkMultipass.IsChecked == true;
        // Conversão rápida: só NVENC (vale nos dois modos — zera o lookahead)
        ChkFast.Visibility = IsNvenc ? Visibility.Visible : Visibility.Collapsed;
        // Reforço de qualidade: só NVENC (AQ/UHQ são recursos do encoder da NVIDIA)
        ChkBoost.Visibility = IsNvenc ? Visibility.Visible : Visibility.Collapsed;
        // Encodes em paralelo: só SVT (NVENC paraleliza pelo pool de placas)
        ParallelPanel.Visibility = IsNvenc ? Visibility.Collapsed : Visibility.Visible;
        // Detecção de cenas: só motor Av1an (av-scenechange) — SVT direto e NVENC não têm
        CmbScenecut.Visibility = IsAv1an ? Visibility.Visible : Visibility.Collapsed;
        UpdateBoostEnabled();
    }

    private void UpdateBoostEnabled() => ChkBoost.IsEnabled = ChkFast.IsChecked != true;

    private void RbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressEvents)
            ApplyModeDependencies();
    }

    private void SldCq_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        CqLabel.Text = CqLabelText((int)e.NewValue);
    }

    /// <summary>No CQ/RF o valor baixo é QUALIDADE alta (estilo HandBrake RF).</summary>
    private string CqLabelText(int cq)
    {
        var t = AppServices.Localizer;
        var quality = cq switch
        {
            <= 14 => t.T("encodes.qVeryHigh"),
            <= 28 => t.T("encodes.qHigh"),
            <= 42 => t.T("encodes.qMedium"),
            <= 53 => t.T("encodes.qLow"),
            _ => t.T("encodes.qVeryLow"),
        };
        return $"{t.T("encodes.cqValue")}: {cq} ({quality})";
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || CmbCodec.SelectedIndex < 0)
            return;

        var cfg = _current with
        {
            Preset = (int)NumPreset.Value,
            UseConstantQuality = RbMode.SelectedIndex == 0,
            Cq = (int)SldCq.Value,
            Multipass = ChkMultipass.IsChecked == true,
            TurboFirstPass = ChkTurbo.IsChecked == true,
            FastConversion = ChkFast.IsChecked == true,
            QualityBoost = ChkBoost.IsChecked == true,
            Tune = (IsNvenc ? TuneNvenc : TuneSvt)[Math.Max(0, CmbTune.SelectedIndex)].Value,
            Profile = Profiles[Math.Max(0, CmbProfile.SelectedIndex)].Value,
            Level = (CmbLevel.SelectedItem?.ToString() ?? "Auto").ToLowerInvariant(), // "Auto" → "auto" (valor, não rótulo)
            ParallelWorkers = Math.Clamp(CmbParallel.SelectedIndex + 1, 1, 3),
            ScenecutMode = Math.Clamp(CmbScenecut.SelectedIndex, 0, 2),
        };

        await AppServices.EncodeConfigs.SaveAsync(cfg);
        SaveStatus.Text = AppServices.Localizer.T("encodes.saved", _codecs[CmbCodec.SelectedIndex].Label);
    }
}
