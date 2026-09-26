using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>ToolsLocator: o fork híbrido é achado por caminho DIRETO em
/// tools\svt-av1-hybrid\ (Locate genérico poderia achar o SvtAv1EncApp stock).</summary>
public class ToolsHybridTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"animebatch-hybrid-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Hybrid_ausente_fica_null_sem_quebrar()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tools"));
        File.WriteAllText(Path.Combine(_root, "tools", "ffmpeg.exe"), "");

        var tools = new ToolsLocator(Path.Combine(_root, "tools"));

        Assert.Null(tools.SvtHybridEncAppPath);
        Assert.Null(tools.HybridPriorFile);
        Assert.NotNull(tools.FfmpegPath);
    }

    [Fact]
    public void Hybrid_com_fork_e_prior_encontra_ambos()
    {
        var hybrid = Path.Combine(_root, "tools", "svt-av1-hybrid");
        Directory.CreateDirectory(hybrid);
        File.WriteAllText(Path.Combine(hybrid, "SvtAv1EncApp.exe"), "");
        File.WriteAllText(Path.Combine(hybrid, "ai_prior.txt"), "");

        var tools = new ToolsLocator(Path.Combine(_root, "tools"));

        Assert.Equal(Path.Combine(hybrid, "SvtAv1EncApp.exe"), tools.SvtHybridEncAppPath);
        Assert.Equal(Path.Combine(hybrid, "ai_prior.txt"), tools.HybridPriorFile);
    }

    [Fact]
    public void Hybrid_sem_prior_fica_so_o_exe()
    {
        var hybrid = Path.Combine(_root, "tools", "svt-av1-hybrid");
        Directory.CreateDirectory(hybrid);
        File.WriteAllText(Path.Combine(hybrid, "SvtAv1EncApp.exe"), "");

        var tools = new ToolsLocator(Path.Combine(_root, "tools"));

        Assert.NotNull(tools.SvtHybridEncAppPath);
        Assert.Null(tools.HybridPriorFile); // sem tabela, o fork roda com IA desligada
    }
}
