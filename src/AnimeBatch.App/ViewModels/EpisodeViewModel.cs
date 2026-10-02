using AnimeBatch.App.Services;
using AnimeBatch.Core.Models;
using AnimeBatch.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeBatch.App.ViewModels;

public partial class EpisodeViewModel : ObservableObject
{
    public string FileName { get; }
    public string FullPath { get; }
    public EpisodeInfo? Probed { get; set; }
    public Series? Series { get; set; }

    /// <summary>Marcado = vai pra fila quando o usuário clicar em "Enfileirar selecionados".</summary>
    [ObservableProperty]
    public partial bool Include { get; set; }

    [ObservableProperty]
    public partial string MatchLabel { get; set; } = "";

    [ObservableProperty]
    public partial string Info { get; set; } = "";

    /// <summary>Este episódio tem grade editada salva (arquivo em chapters-edits) —
    /// o "Resetar Capítulos" do menu ⋯ fica habilitado.</summary>
    [ObservableProperty]
    public partial bool HasChapterEdits { get; set; }

    /// <summary>Este episódio tem Calibragem Automática salva (arquivo em calibracoes) —
    /// habilita a aba Calibragem e faz o enfileirar usar os cortes da calibragem.</summary>
    [ObservableProperty]
    public partial bool HasCalibration { get; set; }

    [ObservableProperty]
    public partial System.Collections.ObjectModel.ObservableCollection<ChapterItemViewModel> Chapters { get; set; } = new();

    /// <summary>Grade PARALELA da Calibragem Automática — blocos por criticidade de bitrate.
    /// Nunca vira capítulo no arquivo final; ao enfileirar com calibragem, são os cortes usados.</summary>
    [ObservableProperty]
    public partial System.Collections.ObjectModel.ObservableCollection<ChapterItemViewModel> CalibrationChapters { get; set; } = new();

    public EpisodeViewModel(string fullPath)
    {
        FullPath = fullPath;
        FileName = System.IO.Path.GetFileName(fullPath);
    }
}

/// <summary>Estado da PARTE no disco para o capítulo: nenhum arquivo, arquivo cujo tempo
/// bate com o capítulo (✓) ou arquivo com tempo diferente (✗ — removível pela lista).</summary>
public enum PartFileStatus
{
    None,
    Ok,
    Mismatch,
}

public partial class ChapterItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    /// <summary>Posição na lista (renumerada ao mover/adicionar/remover).</summary>
    [ObservableProperty]
    public partial int Number { get; set; }

    public string Title { get; init; } = "";

    /// <summary>Rótulo da classe/bitrate — muda quando o codec alterna entre CQ e taxa de bits.</summary>
    [ObservableProperty]
    public partial string TargetLabel { get; set; } = "";

    public BitrateClass Class { get; init; }
    public int TargetKbps { get; init; }
    public double StartSeconds { get; init; }

    /// <summary>Nível da Calibragem Automática deste bloco (null nos capítulos da grade normal).
    /// O TÍTULO do bloco é o nome localizado do nível, mas o nível gravado no JSON é este —
    /// trocar o idioma não quebra a persistência.</summary>
    public CalibrationLevel? Calibration { get; init; }

    /// <summary>Final derivado (início do próximo capítulo / fim do vídeo) — muda quando a
    /// grade é editada; binding OneWay na grade de capítulos.</summary>
    [ObservableProperty]
    public partial double EndSeconds { get; set; }

    /// <summary>"0:00 → 12:34" — recalculado junto com o fim derivado.</summary>
    [ObservableProperty]
    public partial string TimeRange { get; set; } = "";

    /// <summary>Preset do encode só deste capítulo (0 = usa o da config do codec).</summary>
    [ObservableProperty]
    public partial int Preset { get; set; }

    /// <summary>CQ deste capítulo quando o codec está em Qualidade Constante (0 = usa o da config).</summary>
    [ObservableProperty]
    public partial int Cq { get; set; }

    /// <summary>Capítulo temporário: encodeia como parte, mas não vira capítulo no final.</summary>
    [ObservableProperty]
    public partial bool IsTemporary { get; set; }

    /// <summary>Parte deste capítulo já existe na pasta de trabalho? E o tempo bate?
    /// Avaliado pela tela (probe das partes existentes, com cache por arquivo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OkVisibility), nameof(MismatchVisibility), nameof(OkTooltip), nameof(MismatchTooltip))]
    public partial PartFileStatus FileStatus { get; set; } = PartFileStatus.None;

    public Microsoft.UI.Xaml.Visibility TemporaryVisibility =>
        IsTemporary ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility OkVisibility =>
        FileStatus == PartFileStatus.Ok ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility MismatchVisibility =>
        FileStatus == PartFileStatus.Mismatch ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string OkTooltip => AppServices.Localizer.T("episodes.partOkTooltip");

    public string MismatchTooltip => AppServices.Localizer.T("episodes.partMismatchTooltip");

    partial void OnIsTemporaryChanged(bool value) =>
        OnPropertyChanged(nameof(TemporaryVisibility));
}
