using AnimeBatch.Core.Models;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Write-back do bitrate de um capítulo para o padrão da série: o ajuste fino feito no
/// modal da tela de Episódios (encode não satisfatório → muda o kbps) vira o default da
/// próxima encode daquela série.
/// </summary>
public static class SeriesKbpsSync
{
    /// <summary>
    /// Bitrates a gravar na série (null = campo intocado), ou null quando nada muda.
    /// Capítulo temporário nunca escreve (é one-off por definição); cena crítica também
    /// não (é pontual — usa o bitrate da abertura, mas não É a abertura); valor igual ao
    /// atual ou inválido (≤ 0) idem.
    /// </summary>
    public static (int? EpisodeKbps, int? OpeningKbps, int? EndingKbps)? Compute(
        BitrateClass chapterClass, int targetKbps, Series series, bool isTemporary)
    {
        if (isTemporary || chapterClass == BitrateClass.Critical || targetKbps <= 0)
            return null;

        int? episode = null, opening = null, ending = null;
        switch (chapterClass)
        {
            case BitrateClass.Opening when targetKbps != series.OpeningKbps:
                opening = targetKbps;
                break;
            case BitrateClass.Ending when targetKbps != series.EndingKbps:
                ending = targetKbps;
                break;
            case BitrateClass.Episode when targetKbps != series.EpisodeKbps:
                episode = targetKbps;
                break;
        }

        return episode is null && opening is null && ending is null ? null : (episode, opening, ending);
    }
}
