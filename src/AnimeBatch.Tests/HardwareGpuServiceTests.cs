using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class HardwareGpuServiceTests
{
    [Fact]
    public void Parse_e_serialize_vao_e_voltam()
    {
        var cards = new List<HardwareGpuCard>
        {
            new("NVIDIA GeForce RTX 5060 Ti", 2, 16L * 1024 * 1024 * 1024),
            new("NVIDIA GeForce RTX 3060", 1),
        };
        var json = HardwareGpuService.Serialize(cards);
        var parsed = HardwareGpuService.Parse(json);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("NVIDIA GeForce RTX 5060 Ti", parsed[0].Name);
        Assert.Equal(2, parsed[0].Workers);
        Assert.Equal(1, parsed[1].Workers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao é json {")]
    [InlineData("[{\"name\":}]")]
    public void Parse_malformado_ou_vazio_da_lista_vazia(string? json)
    {
        Assert.Empty(HardwareGpuService.Parse(json));
    }

    [Fact]
    public void Parse_limita_workers_e_descarta_sem_nome()
    {
        var parsed = HardwareGpuService.Parse(
            """[{"Name":"A","Workers":9},{"Name":"","Workers":2},{"Name":"B","Workers":0},{"Name":"C","Workers":-3}]""");
        Assert.Equal(3, parsed.Count);
        Assert.Equal(4, parsed[0].Workers);   // clamp 0..4
        Assert.Equal(0, parsed[1].Workers);   // 0 é válido: placa excluída
        Assert.Equal(0, parsed[2].Workers);   // negativo → 0
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel Iris Xe Graphics", true)]
    [InlineData("Intel Arc A770", false)]
    [InlineData("NVIDIA GeForce RTX 5060 Ti", false)]
    [InlineData("AMD Radeon RX 6600", false)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M", true)]
    public void IsIntegrated_exclui_igpu_da_cpu(string name, bool expected)
    {
        Assert.Equal(expected, HardwareGpuService.IsIntegrated(name));
    }

    [Fact]
    public void ExpandWorkers_repete_indice_e_segue_ordem_da_configuracao()
    {
        var cards = new List<HardwareGpuCard>
        {
            new("NVIDIA GeForce RTX 5060 Ti", 2),
            new("NVIDIA GeForce RTX 3060", 1),
        };
        var devices = new List<(int, string)>
        {
            (0, "NVIDIA GeForce RTX 5060 Ti"),
            (1, "Intel(R) UHD Graphics 770"),
            (2, "NVIDIA GeForce RTX 3060"),
        };
        // 2 workers na principal + 1 na segunda
        Assert.Equal([0, 0, 2], HardwareGpuService.ExpandWorkers(cards, devices));
    }

    [Fact]
    public void ExpandWorkers_casa_por_nome_normalizado_e_ignora_desconhecida()
    {
        var cards = new List<HardwareGpuCard>
        {
            new("nvidia   geforce rtx 5060 ti", 2),   // espaços/caps diferentes
            new("RTX 9999 inexistente", 2),
        };
        var devices = new List<(int, string)> { (3, "NVIDIA GeForce RTX 5060 Ti") };
        Assert.Equal([3, 3], HardwareGpuService.ExpandWorkers(cards, devices));
    }

    [Fact]
    public void ExpandWorkers_sem_devices_ou_sem_cards_fica_vazio()
    {
        Assert.Empty(HardwareGpuService.ExpandWorkers(
            [new HardwareGpuCard("A", 2)], []));
        Assert.Empty(HardwareGpuService.ExpandWorkers(
            [], [(0, "A")]));
    }

    [Fact]
    public void ExpandWorkers_card_com_zero_workers_nao_recebe_nada()
    {
        var cards = new List<HardwareGpuCard>
        {
            new("NVIDIA GeForce RTX 4060 Ti", 0),   // excluída pelo usuário
            new("NVIDIA GeForce RTX 5060 Ti", 2),
        };
        var devices = new List<(int, string)>
        {
            (0, "NVIDIA GeForce RTX 4060 Ti"),
            (1, "NVIDIA GeForce RTX 5060 Ti"),
        };
        // só a 5060 Ti entra no pool, com 2 workers — nada vai para a 4060 Ti
        Assert.Equal([1, 1], HardwareGpuService.ExpandWorkers(cards, devices));
    }

    [Fact]
    public void NormalizeName_colapsa_espacos_e_maiusculas()
    {
        Assert.Equal(
            HardwareGpuService.NormalizeName("NVIDIA  GeForce\tRTX 5060 Ti"),
            HardwareGpuService.NormalizeName("  nvidia geforce rtx 5060 ti "));
    }
}
