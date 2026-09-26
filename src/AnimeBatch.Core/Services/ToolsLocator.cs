namespace AnimeBatch.Core.Services;

/// <summary>
/// Localiza os binários externos (ffmpeg, ffprobe, mkvmerge, mkvextract, HandBrakeCLI e as
/// ferramentas de upscale) sem depender de PATH. Ordem de busca:
/// 1. Pasta tools\ do aplicativo (deploy final, tudo embutido);
/// 2. Locais de instalação conhecidos (fallback de desenvolvimento);
/// 3. Pastas extras da setting "tools.extraDirs" (cada instalação aponta os seus);
/// 4. Legado: ffmpeg-* na raiz de D:\ (máquina de desenvolvimento do dono);
/// 5. Variável PATH do sistema.
/// </summary>
public class ToolsLocator
{
    private static readonly (string Name, string[] KnownDirs)[] KnownLocations =
    {
        ("ffmpeg.exe", []),
        ("ffprobe.exe", []),
        ("mkvmerge.exe", [@"C:\Program Files\MKVToolNix", @"C:\Program Files (x86)\MKVToolNix"]),
        ("mkvextract.exe", [@"C:\Program Files\MKVToolNix", @"C:\Program Files (x86)\MKVToolNix"]),
        ("HandBrakeCLI.exe", [@"C:\Program Files\HandBrake"]),
        ("realcugan-ncnn-vulkan.exe", []),
        ("realesrgan-ncnn-vulkan.exe", []),
    };

    private readonly string _toolsDir;
    private readonly IReadOnlyList<string> _extraDirs;

    public ToolsLocator(string? toolsDir = null, IReadOnlyList<string>? extraDirs = null)
    {
        _toolsDir = toolsDir ?? FindToolsDir() ?? Path.Combine(AppContext.BaseDirectory, "tools");
        _extraDirs = extraDirs ?? [];

        FfmpegPath = Locate("ffmpeg.exe");
        FfprobePath = Locate("ffprobe.exe");
        MkvMergePath = Locate("mkvmerge.exe");
        MkvExtractPath = Locate("mkvextract.exe");
        HandBrakeCliPath = Locate("HandBrakeCLI.exe");
        // Upscale é opcional: ausência não impede o resto do app.
        RealCuganPath = Locate("realcugan-ncnn-vulkan.exe");
        RealesrganPath = Locate("realesrgan-ncnn-vulkan.exe");
        // Av1an (opcional): engine de encode com fatiamento por cena. Precisa do
        // SvtAv1EncApp ao lado E do VapourSynth+Python na máquina (DLLs no PATH do filho).
        Av1anPath = Locate("av1an.exe");
        SvtAv1EncAppPath = Locate("SvtAv1EncApp.exe");
        VspipePath = Locate("vspipe.exe");
        // Modelos do motor ONNX (AnimeJaNai): tools\models-onnx na raiz (ou subpasta).
        OnnxModelsDir = Directory.Exists(Path.Combine(_toolsDir, "models-onnx"))
            ? Path.Combine(_toolsDir, "models-onnx")
            : Directory.Exists(_toolsDir)
                ? Directory.EnumerateDirectories(_toolsDir, "models-onnx", SearchOption.AllDirectories).FirstOrDefault()
                : null;
    }

    public string? FfmpegPath { get; }
    public string? FfprobePath { get; }
    public string? MkvMergePath { get; }
    public string? MkvExtractPath { get; }
    public string? HandBrakeCliPath { get; }
    public string? RealCuganPath { get; }
    public string? RealesrganPath { get; }
    public string? OnnxModelsDir { get; }
    public string? Av1anPath { get; }
    public string? SvtAv1EncAppPath { get; }
    public string? VspipePath { get; }

    /// <summary>Pasta tools\ detectada (null se não existe) — base do PATH extra que o
    /// av1an precisa receber para encontrar ffmpeg/SvtAv1EncApp.</summary>
    public string? ToolsDir => Directory.Exists(_toolsDir) ? _toolsDir : null;

    /// <summary>
    /// Procura a pasta tools\ subindo a árvore a partir do executável — cobre o layout de
    /// produção (exe + tools\ juntos) e o de desenvolvimento (bin\...\x\ do repo: são 7
    /// níveis até a raiz do repo, onde tools\ vive — bin\x64\Debug\net9.0-windows…\).
    /// </summary>
    private static string? FindToolsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>Ferramentas essenciais ausentes — o app não inicia o processamento sem elas.</summary>
    public IReadOnlyList<string> MissingEssential =>
        new[]
        {
            ("ffmpeg.exe", FfmpegPath), ("ffprobe.exe", FfprobePath),
            ("mkvmerge.exe", MkvMergePath), ("mkvextract.exe", MkvExtractPath),
            ("HandBrakeCLI.exe", HandBrakeCliPath),
        }.Where(t => t.Item2 is null).Select(t => t.Item1).ToList();

    /// <summary>Ferramentas de upscale ausentes (aviso, não bloqueia).</summary>
    public IReadOnlyList<string> MissingUpscale =>
        new[] { ("realcugan-ncnn-vulkan.exe", RealCuganPath), ("realesrgan-ncnn-vulkan.exe", RealesrganPath) }
            .Where(t => t.Item2 is null).Select(t => t.Item1).ToList();

    private string? Locate(string exeName)
    {
        // 1) Pasta tools\ do app (raiz)
        var candidate = Path.Combine(_toolsDir, exeName);
        if (File.Exists(candidate))
            return candidate;

        // 1b) Qualquer profundidade dentro de tools\ — ferramentas de upscale vivem em
        //     subpastas (com os modelos ao lado), às vezes com pasta de versão no meio
        //     (ex.: tools\realcugan\realcugan-ncnn-vulkan-20220728-windows\*.exe).
        if (Directory.Exists(_toolsDir))
        {
            var found = Directory.EnumerateFiles(_toolsDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
                return found;
        }

        // 2) Locais conhecidos
        var known = KnownLocations.FirstOrDefault(k => k.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        if (known.KnownDirs is { Length: > 0 })
        {
            foreach (var dir in known.KnownDirs)
            {
                var p = Path.Combine(dir, exeName);
                if (File.Exists(p))
                    return p;
            }
        }

        // 3) Pastas extras configuradas (setting "tools.extraDirs") — direto na pasta e,
        //     para ffmpeg/ffprobe, na subpasta bin\ de uma extração comum
        foreach (var dir in _extraDirs)
        {
            var p = Path.Combine(dir, exeName);
            if (File.Exists(p))
                return p;
        }
        if (exeName is "ffmpeg.exe" or "ffprobe.exe")
        {
            foreach (var dir in _extraDirs)
            {
                var p = Path.Combine(dir, "bin", exeName);
                if (File.Exists(p))
                    return p;
            }
        }

        // 4) Legado da máquina do dono: extração BtbN solta na raiz de D:\ (guardado com
        //     Directory.Exists — sem o drive, GetDirectories lança e derrubaria o ctor)
        if (exeName is "ffmpeg.exe" or "ffprobe.exe" && Directory.Exists(@"D:\"))
        {
            foreach (var dir in Directory.GetDirectories(@"D:\", "ffmpeg-*", SearchOption.TopDirectoryOnly))
            {
                var p = Path.Combine(dir, "bin", exeName);
                if (File.Exists(p))
                    return p;
            }
        }

        // 3) PATH do sistema
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir, exeName);
                if (File.Exists(p))
                    return p;
            }
            catch (ArgumentException)
            {
                // segmento de PATH inválido — ignora
            }
        }

        return null;
    }
}
