using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Derivação de fins de capítulo (o usuário só informa o INÍCIO): o final de cada capítulo
/// é o início do próximo na linha do tempo e o do último é a duração do vídeo.
/// </summary>
public class ChapterTimelineTests
{
    [Fact]
    public void Fim_de_cada_capitulo_e_o_inicio_do_proximo_e_o_ultimo_vai_ate_a_duracao()
    {
        // grade: 0→300, 300→600, 600→1400 (duração)
        var ends = ChapterTimeline.DeriveEnds([0, 300, 600], 1400);
        Assert.Equal(new[] { 300.0, 600.0, 1400.0 }, ends);
    }

    [Fact]
    public void A_ordem_da_grade_nao_muda_a_derivacao_so_a_linha_do_tempo()
    {
        // capítulo novo inserido no fim da lista, mas com início no meio do vídeo
        var ends = ChapterTimeline.DeriveEnds([0, 600, 1200, 300], 1400);
        Assert.Equal(300.0, ends[0], 3);  // 0 termina quando 300 começa
        Assert.Equal(1400.0, ends[2], 3); // 1200 vai até o fim do vídeo
        Assert.Equal(600.0, ends[3], 3);  // 300 termina quando 600 começa
    }

    [Fact]
    public void Capitulo_unico_cobre_o_video_inteiro()
    {
        var ends = ChapterTimeline.DeriveEnds([0], 1800);
        Assert.Equal(1800.0, ends[0], 3);
    }

    [Fact]
    public void Inicio_fora_da_duracao_produz_trecho_vazio_sem_explodir()
    {
        var ends = ChapterTimeline.DeriveEnds([0, 1500], 1400);
        Assert.Equal(1400.0, ends[0], 3); // o anterior não passa da duração do vídeo
        Assert.Equal(1400.0, ends[1], 3); // começou depois do fim → termina onde começou
    }
}
