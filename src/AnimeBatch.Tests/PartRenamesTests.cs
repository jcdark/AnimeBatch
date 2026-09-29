using AnimeBatch.Core.Queueing;

namespace AnimeBatch.Tests;

/// <summary>Plano de renomeação das partes quando a grade de capítulos muda — inserir no
/// meio renumera os seguintes e os arquivos precisam acompanhar sem colisão.</summary>
public class PartRenamesTests
{
    private static string Part(int n, string title) => JobPaths.PartFileName(n, title, "Saga of Tanya the Evil - S04E06");

    [Fact]
    public void Inserir_no_meio_desloca_os_seguintes_em_cadeia_sem_colisao()
    {
        // grade do dono: inserir "04 - new chapter" entre 03 e o antigo 04
        var renames = new List<(string From, string To)>
        {
            (Part(4, "Episode"), Part(5, "Episode")),
            (Part(5, "Credits"), Part(6, "Credits")),
            (Part(6, "Episode"), Part(7, "Episode")),
        };

        var plan = PartRenames.Plan(renames);

        // duas fases: todas as origens vão para o temporário ANTES de qualquer destino
        Assert.Equal(6, plan.Count);
        Assert.All(plan.Take(3), s => Assert.EndsWith(PartRenames.TempSuffix, s.To));
        Assert.All(plan.Skip(3), s => Assert.EndsWith(PartRenames.TempSuffix, s.From));
        // fase 2 leva cada temporário ao destino correto
        Assert.Contains((Part(4, "Episode") + PartRenames.TempSuffix, Part(5, "Episode")), plan);
        Assert.Contains((Part(5, "Credits") + PartRenames.TempSuffix, Part(6, "Credits")), plan);
        Assert.Contains((Part(6, "Episode") + PartRenames.TempSuffix, Part(7, "Episode")), plan);
    }

    [Fact]
    public void Troca_de_posicao_entre_dois_capitulos_nao_colide()
    {
        var renames = new List<(string From, string To)>
        {
            (Part(2, "Intro"), Part(3, "Intro")),
            (Part(3, "Episode"), Part(2, "Episode")),
        };

        var plan = PartRenames.Plan(renames);

        // origens viram temporários antes de qualquer destino ocupado
        Assert.Equal(4, plan.Count);
        Assert.Equal(Part(2, "Intro") + PartRenames.TempSuffix, plan[0].To);
        Assert.Equal(Part(3, "Episode") + PartRenames.TempSuffix, plan[1].To);
    }

    [Fact]
    public void Movimento_identico_e_descartado()
    {
        var plan = PartRenames.Plan([(Part(1, "Episode"), Part(1, "Episode"))]);
        Assert.Empty(plan);
    }
}
