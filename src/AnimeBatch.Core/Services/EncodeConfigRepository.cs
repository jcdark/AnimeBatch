using System.Text.Json;

namespace AnimeBatch.Core.Services;

/// <summary>
/// Configuração de um encode (por codec), editável na aba Encodes e persistida no banco
/// (Settings, chave "encode.cfg.{code}").
/// </summary>
public record CodecEncodeConfig
{
    public string Code { get; set; } = "";

    /// <summary>SVT: 1–13 (maior = melhor/lento); NVENC: 1–7 (p1–p7).</summary>
    public int Preset { get; set; } = 5;

    /// <summary>true = Qualidade Constante (CQ/RF); false = Taxa de Bits Média (kbps do capítulo).</summary>
    public bool UseConstantQuality { get; set; }

    /// <summary>Valor de CQ/RF quando UseConstantQuality (0–63, estilo HandBrake RF).</summary>
    public int Cq { get; set; } = 22;

    /// <summary>SVT: 2-pass de verdade; NVENC: -multipass fullres.</summary>
    public bool Multipass { get; set; } = true;

    /// <summary>SVT: primeira passada num preset mais rápido. NVENC: ignorado.</summary>
    public bool TurboFirstPass { get; set; } = true;

    /// <summary>NVENC: none/hq/ll/ull/lossless; SVT: none/0/1.</summary>
    public string Tune { get; set; } = "none";

    /// <summary>NVENC: zera o lookahead (encode mais rápido). SVT: ignorado.</summary>
    public bool FastConversion { get; set; }

    /// <summary>
    /// NVENC: reforço de qualidade — AQ espacial/temporal, tune UHQ, filtro temporal e
    /// lookahead_level (análise estendida, encode mais lento). É o que aproxima o NVENC do
    /// AV1 software e ataca os macroblocos à mesma taxa. SVT: ignorado.
    /// </summary>
    public bool QualityBoost { get; set; } = true;

    /// <summary>"none"/"0"/"1"/"2" (0=Main, 1=High, 2=Professional).</summary>
    public string Profile { get; set; } = "0";

    /// <summary>"auto" ou o nível (ex.: "4.0").</summary>
    public string Level { get; set; } = "auto";

    public static CodecEncodeConfig Default(string code) => new()
    {
        Code = code,
        Preset = code.StartsWith("nvenc", StringComparison.Ordinal) ? 5 : 6,
        Tune = code.StartsWith("nvenc", StringComparison.Ordinal) ? "hq" : "none",
    };

    public static string[] KnownCodes =>
    [
        "svt_av1", "svt_av1_10bit", "nvenc_av1", "nvenc_av1_10bit",
    ];
}

/// <summary>Persistência das configurações de encode (JSON no banco local).</summary>
public class EncodeConfigRepository(SettingsRepository settings)
{
    private const string KeyPrefix = "encode.cfg.";

    private readonly SettingsRepository _settings = settings;

    public async Task<CodecEncodeConfig> GetAsync(string code)
    {
        var json = await _settings.GetAsync(KeyPrefix + code).ConfigureAwait(false);
        if (json is null)
            return CodecEncodeConfig.Default(code);

        try
        {
            var cfg = JsonSerializer.Deserialize<CodecEncodeConfig>(json);
            return cfg is null ? CodecEncodeConfig.Default(code) : cfg with { Code = code };
        }
        catch (JsonException)
        {
            return CodecEncodeConfig.Default(code);
        }
    }

    public async Task SaveAsync(CodecEncodeConfig cfg)
    {
        await _settings.SetAsync(KeyPrefix + cfg.Code, JsonSerializer.Serialize(cfg)).ConfigureAwait(false);
    }
}
