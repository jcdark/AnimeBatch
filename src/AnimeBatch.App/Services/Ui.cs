using AnimeBatch.Core.Models;

namespace AnimeBatch.App.Services;

/// <summary>
/// Helpers compartilhados entre páginas (antes duplicados): mapeamentos combo↔valor do
/// upscale, formatador de tamanho e captura de exceção de handlers async.
/// </summary>
internal static class Ui
{
    /// <summary>Combo resolução → altura alvo (índice 1 = 1080, o default do combo).</summary>
    public static int HeightFromResolutionIndex(int index) => index switch
    {
        0 => 720,
        2 => 1440,
        3 => 2160,
        _ => 1080,
    };

    /// <summary>Altura alvo → índice do combo resolução (valor fora da lista cai em 1080).</summary>
    public static int ResolutionIndexFromHeight(int? height) => height switch
    {
        720 => 0,
        1440 => 2,
        2160 => 3,
        _ => 1,
    };

    /// <summary>Combo modelo → código do motor de upscale.</summary>
    public static string ModelFromIndex(int index) => index switch
    {
        1 => "realesrgan",
        2 => "onnx",
        _ => "realcugan",
    };

    /// <summary>Código do motor → índice do combo modelo.</summary>
    public static int ModelIndexFromCode(string? code) => code switch
    {
        "realesrgan" => 1,
        "onnx" => 2,
        _ => 0,
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// Roda o corpo de um handler async capturando exceção: grava no crash.log e entrega o
    /// erro pra página exibir (InfoBar). Sem isso, qualquer falha num async void morre em
    /// silêncio no crash.log e o usuário não vê nada. Retorno void de propósito: o chamador
    /// dispara e esquece (o próprio método é quem garante o tratamento).
    /// </summary>
    public static void Safe(Func<Task> action, Action<Exception> onError) =>
        _ = RunSafeAsync(action, onError);

    private static async Task RunSafeAsync(Func<Task> action, Action<Exception> onError)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppServices.LogCrash("UI", ex);
            onError(ex);
        }
    }
}
