using AnimeBatch.Core.Services;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AnimeBatch.Tests;

/// <summary>
/// Infra compartilhada dos testes de integração (antes duplicada entre os dois arquivos).
/// tools\ vem da raiz do repo; SEM ele os testes PULAM com motivo claro — um clone novo
/// roda dotnet test sem precisar baixar ~500 MB de binários (scripts/setup-tools.ps1 baixa).
/// </summary>
public static class IntegrationHelpers
{
    /// <summary>Acha tools\ da raiz do repo subindo do bin de teste (a ToolsLocator padrão
    /// sobe só 6 níveis, um a menos do que o bin\...\net9.0 precisa).</summary>
    public static string? FindRepoToolsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static readonly ToolsLocator Tools = new(FindRepoToolsDir());

    public static bool HasFfmpeg => Tools.FfmpegPath is not null && Tools.FfprobePath is not null;
    public static bool HasCugan => Tools.RealCuganPath is not null;
    public static bool HasOnnxModels => Tools.OnnxModelsDir is not null;
    public static bool HasNvidiaSmi => DmlDeviceCalibration.FindNvidiaSmi() is not null;

    public static void SkipIfNoFfmpeg() =>
        Skip.IfNot(HasFfmpeg, "ffmpeg/ffprobe não encontrados em tools\\ — rode scripts/setup-tools.ps1");

    public static void SkipIfNoCugan() =>
        Skip.IfNot(HasFfmpeg && HasCugan, "realcugan-ncnn-vulkan não encontrado em tools\\ — rode scripts/setup-tools.ps1");

    public static void SkipIfNoOnnxModels() =>
        Skip.IfNot(HasFfmpeg && HasOnnxModels, "tools\\models-onnx não encontrado — rode scripts/setup-tools.ps1");

    public static void SkipIfNoNvenc()
    {
        Skip.IfNot(HasFfmpeg && HasNvidiaSmi, "sem nvidia-smi/NVENC nesta máquina — o teste precisa de uma placa NVIDIA");
        Skip.IfNot(NvencDriverAceitaEncode(), "driver NVIDIA antigo demais para o ffmpeg de tools\\ " +
            "(exige ≥ 610, NVENC API 13.1) — atualize o driver da placa para rodar encode NVENC");
    }

    private static bool? _nvencDriverOk;

    /// <summary>Probe 1x por execução: 2 frames com av1_nvenc; o ffmpeg recusa driver velho
    /// com "minimum required Nvidia driver" no stderr (que a exceção do RunFfmpeg carrega).
    /// Mantém o teste PULANDO (não falhando) quando a placa existe mas o driver está velho —
    /// sem esconder regressões reais. Este projeto só usa NVENC AV1, então av1_nvenc é o
    /// probe certo: sem ele o caminho NVENC do app não roda de verdade.</summary>
    private static bool NvencDriverAceitaEncode() => _nvencDriverOk ??= ProbeNvenc();

    private static bool ProbeNvenc()
    {
        // Fonte lavfi direto no encoder: nenhum arquivo em disco e linha de comando
        // 100% constante. A recusa de driver velho acontece na abertura do encoder,
        // com qualquer entrada — o ffmpeg sai com erro e o stderr traz o motivo.
        try
        {
            RunFfmpeg("-f lavfi -i testsrc2=size=320x240:rate=24:duration=1 -c:v av1_nvenc -f null -");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            return !ex.Message.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Roda um ffmpeg de preparação (vídeo sintético lavfi); falha se sair com erro.</summary>
    public static void RunFfmpeg(string args)
    {
        var psi = new ProcessStartInfo(Tools.FfmpegPath!)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in SplitArgs(args))
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg de teste falhou: {stderr}");
    }

    public static string Quote(string s) => "\"" + s + "\"";

    public static IEnumerable<string> SplitArgs(string line)
    {
        // parser simples: tokens entre aspas ficam inteiros
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }
}
