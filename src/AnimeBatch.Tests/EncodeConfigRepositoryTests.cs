using System.Text.Json;
using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Regra de compatibilidade da config de encode (regra firme do AGENTS.md): configs salvas
/// por versões antigas SEM as chaves novas precisam continuar funcionando — a desserialização
/// ignora o que falta e o property initializer do record supre o default.
/// </summary>
public class EncodeConfigRepositoryTests
{
    [Fact]
    public void Config_antiga_sem_ParallelWorkers_cai_no_default_1_sequencial()
    {
        var cfg = JsonSerializer.Deserialize<CodecEncodeConfig>(
            """{"Code":"svt_av1","Preset":6,"Multipass":true,"TurboFirstPass":true}""");

        Assert.NotNull(cfg);
        Assert.Equal(1, cfg!.EffectiveParallelWorkers);
        Assert.Equal(6, cfg.Preset); // o resto do JSON antigo segue intacto
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    [InlineData(-1, 1)]
    public void ParallelWorkers_fora_da_faixa_da_ui_faz_clamp_1_a_3(int saved, int expected)
    {
        var cfg = new CodecEncodeConfig { ParallelWorkers = saved };
        Assert.Equal(expected, cfg.EffectiveParallelWorkers);
    }
}
