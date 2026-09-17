using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class NvencGpuProbeTests
{
    [Fact]
    public void Parse_linhas_do_nvidia_smi()
    {
        var devices = NvencGpuProbe.ParseLines(new[]
        {
            "0, NVIDIA GeForce RTX 4060 Ti",
            "1, NVIDIA GeForce RTX 5060 Ti",
            "",
            "lixo sem virgula",
            "x, Nome invalido",
        });

        Assert.Equal(2, devices.Count);
        Assert.Equal((0, "NVIDIA GeForce RTX 4060 Ti"), devices[0]);
        Assert.Equal((1, "NVIDIA GeForce RTX 5060 Ti"), devices[1]);
    }

    [Fact]
    public void Resolve_workers_expande_por_nome_no_espaco_nvenc()
    {
        var cards = HardwareGpuService.Serialize(
        [
            new HardwareGpuCard("NVIDIA GeForce RTX 4060 Ti", 2),
            new HardwareGpuCard("NVIDIA GeForce RTX 5060 Ti", 1),
        ]);
        var devices = NvencGpuProbe.ParseLines(new[]
        {
            "0, NVIDIA GeForce RTX 4060 Ti",
            "1, NVIDIA GeForce RTX 5060 Ti",
        });

        var pool = NvencGpuProbe.ResolveWorkers(cards, devices);

        Assert.NotNull(pool);
        // 2 workers na 4060 Ti (nvenc 0) + 1 na 5060 Ti (nvenc 1)
        Assert.Equal(new[] { 0, 0, 1 }, pool);
    }

    [Fact]
    public void Sem_configuracao_ou_sem_match_devolve_null()
    {
        var devices = NvencGpuProbe.ParseLines(new[] { "0, NVIDIA GeForce RTX 4060 Ti" });

        // sem configuração = automático no motor = encode sequencial
        Assert.Null(NvencGpuProbe.ResolveWorkers(null, devices));

        // configurada mas nenhuma placa bate com o nvidia-smi → null também (config velha)
        var cards = HardwareGpuService.Serialize([new HardwareGpuCard("AMD Radeon RX 9999", 2)]);
        Assert.Null(NvencGpuProbe.ResolveWorkers(cards, devices));
    }

    [Fact]
    public void Placa_com_zero_workers_fica_de_fora_do_pool()
    {
        var cards = HardwareGpuService.Serialize(
        [
            new HardwareGpuCard("NVIDIA GeForce RTX 4060 Ti", 0),   // excluída
            new HardwareGpuCard("NVIDIA GeForce RTX 5060 Ti", 2),
        ]);
        var devices = NvencGpuProbe.ParseLines(new[]
        {
            "0, NVIDIA GeForce RTX 4060 Ti",
            "1, NVIDIA GeForce RTX 5060 Ti",
        });

        var pool = NvencGpuProbe.ResolveWorkers(cards, devices);
        Assert.NotNull(pool);
        Assert.Equal(new[] { 1, 1 }, pool); // nada vai para a 4060 Ti (nvenc 0)
    }

    [Fact]
    public void Todas_as_placas_zeradas_devolve_lista_vazia_e_nao_auto()
    {
        var cards = HardwareGpuService.Serialize(
        [
            new HardwareGpuCard("NVIDIA GeForce RTX 4060 Ti", 0),
            new HardwareGpuCard("NVIDIA GeForce RTX 5060 Ti", 0),
        ]);
        var devices = NvencGpuProbe.ParseLines(new[]
        {
            "0, NVIDIA GeForce RTX 4060 Ti",
            "1, NVIDIA GeForce RTX 5060 Ti",
        });

        // vazio = intenção EXPLÍCITA (o chamador derruba o job com erro claro);
        // null cairia no automático e usaria exatamente a placa que foi excluída
        var pool = NvencGpuProbe.ResolveWorkers(cards, devices);
        Assert.NotNull(pool);
        Assert.Empty(pool);
    }
}
