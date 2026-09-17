namespace AnimeBatch.Core.Services;

/// <summary>
/// Traduz a configuração de workers por placa (tela Hardware (GPU), guardada por NOME)
/// para a lista de índices COM REPETIÇÃO do pool de workers, no espaço de índices do
/// motor: Vulkan para o ncnn (realcugan/realesrgan) e DirectML para o ONNX.
/// null = não deu para resolver (cai no fallback: setting antiga ou automático).
/// Lista VAZIA = intenção explícita: o usuário zerou as placas — nada pode rodar nelas.
/// </summary>
public static class GpuSelector
{
    /// <summary>Regra comum de fechamento: pool achado → usa; vazio é "zero explícito"
    /// (algum card com 0 workers na config) quando há essa intenção marcada, senão é
    /// config velha (placa trocada/renomeada) → null deixa cair no automático.</summary>
    public static IReadOnlyList<int>? Finish(IReadOnlyList<HardwareGpuCard> cards, IReadOnlyList<int> expanded)
    {
        if (expanded.Count > 0)
            return expanded;
        return cards.Any(c => c.Workers == 0) ? [] : null;
    }

    public static async Task<IReadOnlyList<int>?> ResolveNcnnAsync(
        IReadOnlyList<HardwareGpuCard> cards, string? upscalerExePath)
    {
        var devices = await VulkanGpuProbe.EnsureDetectedAsync(upscalerExePath, CancellationToken.None)
            .ConfigureAwait(false);
        if (devices.Count == 0)
            return null;
        return Finish(cards, HardwareGpuService.ExpandWorkers(cards, devices));
    }

    public static IReadOnlyList<int>? ResolveOnnx(IReadOnlyList<HardwareGpuCard> cards, string? onnxModelsDir)
    {
        // Caminho normal: a factory DXGI dá índice + nome de cada placa
        var adapters = DxgiGpuProbe.Enumerate();
        if (adapters.Count > 0)
            return Finish(cards, HardwareGpuService.ExpandWorkers(
                cards, [.. adapters.Select(a => (a.Index, a.Name))]));

        // Factory quebrada (Insider): calibra ids DML por delta de VRAM no nvidia-smi
        if (string.IsNullOrWhiteSpace(onnxModelsDir))
            return null;
        var calibrated = DmlDeviceCalibration.ResolveDeviceNames(onnxModelsDir);
        if (calibrated.Count == 0)
            return null;
        return Finish(cards, HardwareGpuService.ExpandWorkers(cards, calibrated));
    }
}
