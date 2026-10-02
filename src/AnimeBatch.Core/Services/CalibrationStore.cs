using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeBatch.Core.Models;

namespace AnimeBatch.Core.Services;

/// <summary>Um bloco da grade da Calibragem Automática, como salvo no arquivo JSON.
/// Preset/Cq = 0 significa "não informado" (usa a config do codec). Level é o
/// CalibrationLevel como texto (invariante de cultura/idioma — o nome exibido vem do i18n).</summary>
public record CalibrationBlockRecord(
    int Number,
    string Level,
    double StartSeconds,
    double EndSeconds,
    int TargetKbps,
    bool Include,
    int Preset = 0,
    int Cq = 0);

/// <summary>Envelope completo do JSON de calibragem (devolvido pelo Load).</summary>
public record CalibrationData(
    string SourceFile,
    DateTime SavedAt,
    int AnalysisKbps,
    IReadOnlyList<CalibrationBlockRecord> Blocks);

/// <summary>
/// Arquivos da Calibragem Automática (%LOCALAPPDATA%\AnimeBatch\calibracoes): a grade de
/// blocos por nível de criticidade fica salva por vídeo de origem, PARALELA aos capítulos
/// normais — ao enfileirar com calibragem, os cortes saem daqui (blocos como partes
/// temporárias) e os capítulos do arquivo final continuam sendo os da grade normal.
/// </summary>
public class CalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dir;

    public CalibrationStore(string? dir = null) => _dir = dir ?? Data.AppDataPaths.CalibrationsDir;

    /// <summary>Mesma convenção de nome do ChapterEditsStore: stem sanitizado + 8 hex do
    /// SHA-256 do caminho completo (mesclagem previsível entre as duas grades).</summary>
    public string PathFor(string sourceFile)
    {
        var full = Path.GetFullPath(sourceFile);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())));
        var stem = ChapterService.SanitizeTitle(Path.GetFileNameWithoutExtension(full));
        return Path.Combine(_dir, $"{stem}-{hash[..8].ToLowerInvariant()}.json");
    }

    public bool ExistsFor(string sourceFile) => File.Exists(PathFor(sourceFile));

    /// <summary>Grava a grade de calibragem do episódio (sobrescreve).</summary>
    public void Save(string sourceFile, int analysisKbps, IReadOnlyList<CalibrationBlockRecord> blocks)
    {
        Directory.CreateDirectory(_dir);
        var payload = new
        {
            SourceFile = Path.GetFullPath(sourceFile),
            SavedAt = DateTime.Now,
            AnalysisKbps = analysisKbps,
            Blocks = blocks,
        };
        File.WriteAllText(PathFor(sourceFile), JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>Lê a calibragem salva; null se não existe ou o JSON não pôde ser lido.</summary>
    public CalibrationData? Load(string sourceFile)
    {
        try
        {
            var path = PathFor(sourceFile);
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("Blocks", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var blocks = new List<CalibrationBlockRecord>();
            foreach (var b in arr.EnumerateArray())
            {
                blocks.Add(new CalibrationBlockRecord(
                    b.TryGetProperty("Number", out var n) && n.TryGetInt32(out var nv) ? nv : 0,
                    b.TryGetProperty("Level", out var l) ? l.GetString() ?? "Normal" : "Normal",
                    b.TryGetProperty("StartSeconds", out var s) && s.TryGetDouble(out var sv) ? sv : 0,
                    b.TryGetProperty("EndSeconds", out var e) && e.TryGetDouble(out var ev) ? ev : 0,
                    b.TryGetProperty("TargetKbps", out var k) && k.TryGetInt32(out var kv) ? kv : 0,
                    !b.TryGetProperty("Include", out var inc) || inc.ValueKind != JsonValueKind.False,
                    b.TryGetProperty("Preset", out var pr) && pr.TryGetInt32(out var pv) ? pv : 0,
                    b.TryGetProperty("Cq", out var cq) && cq.TryGetInt32(out var cv) ? cv : 0));
            }
            return new CalibrationData(
                root.TryGetProperty("SourceFile", out var sf) ? sf.GetString() ?? "" : "",
                root.TryGetProperty("SavedAt", out var sa) && sa.TryGetDateTime(out var dt) ? dt : DateTime.MinValue,
                root.TryGetProperty("AnalysisKbps", out var ak) && ak.TryGetInt32(out var av) ? av : 0,
                blocks);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // arquivo corrompido/meio-escrito: cai no comportamento "sem calibragem" em vez de derrubar a tela
            return null;
        }
    }

    /// <summary>Apaga o arquivo de calibragem ("Excluir Calibragem" → volta a usar os capítulos). true se apagou algo.</summary>
    public bool Delete(string sourceFile)
    {
        var path = PathFor(sourceFile);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Converte o texto do nível gravado de volta ao enum (legado/estranho vira Normal).</summary>
    public static CalibrationLevel ParseLevel(string? value) =>
        Enum.TryParse<CalibrationLevel>(value, ignoreCase: true, out var l) ? l : CalibrationLevel.Normal;

    /// <summary>Nível como texto para gravar no JSON (invariante, independe de cultura/idioma).</summary>
    public static string FormatLevel(CalibrationLevel level) => level.ToString();
}
