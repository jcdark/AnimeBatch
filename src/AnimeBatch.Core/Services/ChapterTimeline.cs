namespace AnimeBatch.Core.Services;

/// <summary>
/// Derivação de tempos de capítulos da grade de Episódios: o usuário informa SÓ o tempo
/// inicial; o final de cada capítulo é o início do próximo (no tempo) e o do último é a
/// duração do vídeo. Independente da ordem de exibição da grade — olha só a linha do tempo.
/// </summary>
public static class ChapterTimeline
{
    /// <summary>Calcula o final de cada capítulo a partir da lista de inícios e da duração
    /// do vídeo. Devolve os fins na MESMA ordem da entrada. Início >= duração recebe a
    /// própria duração (trecho vazio — a validação da tela rejeita antes disso); inícios
    /// duplicados produzem trecho vazio para o primeiro deles.</summary>
    public static double[] DeriveEnds(IReadOnlyList<double> starts, double duration)
    {
        var ends = new double[starts.Count];
        for (var i = 0; i < starts.Count; i++)
        {
            var next = duration;
            for (var j = 0; j < starts.Count; j++)
            {
                if (j != i && starts[j] > starts[i] && starts[j] < next)
                    next = starts[j];
            }
            ends[i] = next;
        }
        return ends;
    }
}
