namespace AnimeBatch.Core.Models;

/// <summary>Classe de bitrate do capítulo: Episode = normal, Opening = abertura (OP), Ending = encerramento/créditos,
/// Critical = cena marcada como crítica (usa bitrate alto mas NÃO vira capítulo no arquivo final).
/// Os valores numéricos estão gravados no banco (JobItems.Class) — não reordenar.</summary>
public enum BitrateClass
{
    Episode = 0,
    Opening = 1,
    Ending = 2,
    Critical = 3
}

public enum KeywordCategory
{
    Op,
    End
}

public enum JobState
{
    Pending,
    Running,
    Paused,
    Done,
    Error,
    Cancelled
}

public enum JobItemState
{
    Pending,
    Upcaling,
    Encoding,
    Remuxing,
    Done,
    Error,
    Skipped
}

/// <summary>Resolução-alvo do upscale.</summary>
public static class UpscaleResolutions
{
    public static readonly (string Label, int Height)[] Options =
    [
        ("HD (720p)", 720),
        ("Full HD (1080p)", 1080),
        ("2K (1440p)", 1440),
        ("4K (2160p)", 2160),
    ];
}

/// <summary>O que fazer com upscale no processamento do episódio.</summary>
public enum UpscaleMode
{
    /// <summary>Só encode, sem upscale.</summary>
    None = 0,

    /// <summary>Apenas upscaling: gera o vídeo ampliado, sem dividir/encodar por capítulos.</summary>
    Only = 1,

    /// <summary>Upscaling seguido do encode por capítulos.</summary>
    WithEncode = 2
}

/// <summary>Codecs de vídeo suportados no encode (rótulo + código interno gravado no job).</summary>
public static class VideoCodecOptions
{
    public static readonly (string Label, string Code)[] Options =
    [
        ("AV1", "svt_av1"),
        ("AV1 10bits", "svt_av1_10bit"),
        ("AV1 NVENC", "nvenc_av1"),
        ("AV1 10bits NVENC", "nvenc_av1_10bit"),
    ];
}
