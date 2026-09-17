using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnimeBatch.Core.Services;

/// <summary>Configuração de UMA placa dedicada na tela Hardware (GPU).</summary>
/// <param name="Name">Nome da placa como os probes reportam (Vulkan/DXGI/nvidia-smi usam a mesma forma).</param>
/// <param name="Workers">0 = NADA vai para esta placa; 1 = um worker; 2 = dois workers (mais paralelismo).</param>
/// <param name="MemoryBytes">VRAM dedicada quando conhecida (só para exibição).</param>
public record HardwareGpuCard(string Name, int Workers, long MemoryBytes = 0);

/// <summary>
/// Configuração de workers por placa FÍSICA (tela Hardware (GPU), setting "hardware.gpus").
/// A configuração é guardada POR NOME: os índices mudam conforme o motor (Vulkan para o
/// ncnn, DirectML para o ONNX) e por máquina — o nome não. Placas integradas (iGPU da CPU)
/// ficam de fora da tela e do processamento.
/// </summary>
public static partial class HardwareGpuService
{
    public const string SettingKey = "hardware.gpus";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>JSON malformado/vazio → lista vazia (config ausente = automático).</summary>
    public static IReadOnlyList<HardwareGpuCard> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var cards = JsonSerializer.Deserialize<List<HardwareGpuCard>>(json);
            if (cards is null)
                return [];
            return [.. cards
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => c with { Workers = Math.Clamp(c.Workers, 0, 4) })];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<HardwareGpuCard> cards) =>
        JsonSerializer.Serialize(cards, JsonOpts);

    /// <summary>Chave de comparação de nomes: minúsculas e espaços colapsados
    /// ("NVIDIA  GeForce  RTX 5060 Ti" == "nvidia geforce rtx 5060 ti").</summary>
    public static string NormalizeName(string name) =>
        Whitespace().Replace((name ?? "").Trim().ToLowerInvariant(), " ");

    private static bool SameCard(string a, string b)
    {
        var na = NormalizeName(a);
        var nb = NormalizeName(b);
        return na == nb || (na.Length > 0 && (nb.Contains(na, StringComparison.Ordinal) || na.Contains(nb, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Placa INTEGRADA (iGPU da CPU/placa-mãe) não entra na tela nem no pool:
    /// Intel só é dedicada quando é Arc; nomes UHD/Iris/"Radeon(TM) Graphics"/Radeon xxxM são iGPU.
    /// </summary>
    public static bool IsIntegrated(string name)
    {
        var n = name ?? "";
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return !n.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        if (n.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Iris", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) &&
            (n.Contains("(TM)", StringComparison.OrdinalIgnoreCase) ||
             n.EndsWith("Graphics", StringComparison.OrdinalIgnoreCase) ||
             Regex.IsMatch(n, @"Radeon\s+\d{3}M\b")))
            return true;
        return false;
    }

    /// <summary>
    /// Traduz a configuração (por nome) para a lista de índices COM REPETIÇÃO que o pool de
    /// workers consome: card com 2 workers → o índice entra 2x; card com 0 → não entra NADA
    /// (placa excluída). Ordem segue a configuração. Placas não encontradas nos devices são
    /// ignoradas; nenhum match → lista vazia (auto).
    /// </summary>
    public static IReadOnlyList<int> ExpandWorkers(
        IReadOnlyList<HardwareGpuCard> cards,
        IReadOnlyList<(int Index, string Name)> devices)
    {
        var result = new List<int>();
        if (cards.Count == 0 || devices.Count == 0)
            return result;

        foreach (var card in cards)
        {
            var match = devices.FirstOrDefault(d => SameCard(card.Name, d.Name));
            if (match.Name is null)
                continue; // default da tupla = nenhum device com esse nome — ignora a placa
            for (var i = 0; i < card.Workers; i++)
                result.Add(match.Index);
        }
        return result;
    }
}
