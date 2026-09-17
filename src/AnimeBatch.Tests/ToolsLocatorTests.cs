using AnimeBatch.Core.Services;
using Xunit;

namespace AnimeBatch.Tests;

public class ToolsLocatorTests : IDisposable
{
    private readonly string _root;

    public ToolsLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"animetools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string CreateTool(params string[] relativePath)
    {
        var full = Path.Combine([_root, .. relativePath]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "stub");
        return full;
    }

    [Fact]
    public void Encontra_exe_na_raiz_de_tools()
    {
        var expected = CreateTool("tools", "ffmpeg.exe");

        var locator = new ToolsLocator(Path.Combine(_root, "tools"));

        Assert.Equal(expected, locator.FfmpegPath);
    }

    [Fact]
    public void Encontra_exe_em_subpasta_de_tools()
    {
        var expected = CreateTool("tools", "realcugan", "realcugan-ncnn-vulkan.exe");
        var expected2 = CreateTool("tools", "realesrgan", "realesrgan-ncnn-vulkan.exe");

        var locator = new ToolsLocator(Path.Combine(_root, "tools"));

        Assert.Equal(expected, locator.RealCuganPath);
        Assert.Equal(expected2, locator.RealesrganPath);
    }

    [Fact]
    public void Sem_ferramentas_locais_upscale_fica_ausente_e_essenciais_vem_de_fallback()
    {
        // A pasta do locator está vazia, mas os fallbacks (Program Files, D:\ffmpeg*, PATH)
        // podem achar os essenciais — asserção independente de ambiente:
        var locator = new ToolsLocator(Path.Combine(_root, "vazio"));

        Assert.Equal(2, locator.MissingUpscale.Count);
        Assert.DoesNotContain(locator.MissingEssential, m => m.Contains("realcugan"));
        Assert.DoesNotContain(locator.MissingEssential, m => m.Contains("realesrgan"));
    }
}
