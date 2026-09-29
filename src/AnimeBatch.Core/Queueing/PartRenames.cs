using AnimeBatch.Core.Services;

namespace AnimeBatch.Core.Queueing;

/// <summary>
/// Plano de renomeação das partes quando a grade de capítulos muda: inserir um capítulo
/// no meio RENUMERA os seguintes (os arquivos em disco precisam acompanhar: "04 - Episode"
/// vira "05 - Episode", e assim por diante); editar título/posição renomeia um só; remover
/// desloca os pares para baixo. A renomeação em DUAS FASES (origem → temporário → destino)
/// elimina colisão mesmo quando um destino é origem de outro movimento — inserir no meio
/// desloca uma cadeia inteira, e trocas/rotações também são seguras.
/// </summary>
public static class PartRenames
{
    /// <summary>Sufixo da fase temporária. Invólucro inválido como nome normal de parte.</summary>
    public const string TempSuffix = ".~ren";

    /// <summary>Passos ordenados da renomeação. Movimentos idênticos (origem = destino) são
    /// descartados; cada passo depende do anterior para o MESMO arquivo — executar em ordem.</summary>
    public static IReadOnlyList<(string From, string To)> Plan(IEnumerable<(string From, string To)> renames)
    {
        var moves = renames
            .Where(r => !string.Equals(r.From, r.To, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var steps = new List<(string From, string To)>(moves.Count * 2);
        foreach (var (from, _) in moves)
            steps.Add((from, from + TempSuffix));
        foreach (var (from, to) in moves)
            steps.Add((from + TempSuffix, to));
        return steps;
    }
}
