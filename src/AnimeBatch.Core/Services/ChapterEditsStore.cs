using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeBatch.Core.Models;

namespace AnimeBatch.Core.Services;

/// <summary>Um capítulo da grade editada de um episódio, como salvo no arquivo JSON.
/// Preset/Cq = 0 significa "não informado" (usa a config do codec) — 0 não é valor válido
/// de preset nem de CQ, então não colide com override real.</summary>
public record ChapterEditRecord(
    int Number,
    string Title,
    double StartSeconds,
    double EndSeconds,
    string Class,
    int TargetKbps,
    bool Include,
    bool IsTemporary,
    int Preset = 0,
    int Cq = 0);

/// <summary>
/// Arquivos de capítulos editados (%LOCALAPPDATA%\AnimeBatch\chapters-edits): ao adicionar
/// ou editar um capítulo na tela de Episódios, a grade inteira é salva em JSON para o
/// vídeo de origem; ao reabrir o episódio, a grade vem do ARQUIVO e não dos capítulos do
/// vídeo (bitrates da edição ficam gravados junto). Apagar o arquivo = voltar aos
/// capítulos originais do vídeo com bitrates da série ("Resetar Capítulos").
/// </summary>
public class ChapterEditsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dir;

    public ChapterEditsStore(string? dir = null) => _dir = dir ?? Data.AppDataPaths.ChaptersEditsDir;

    /// <summary>Nome do arquivo de edição de um vídeo: nome do arquivo sanitizado + 8 hex
    /// do SHA-256 do caminho completo (em minúsculas) — legível e sem colisão entre pastas.</summary>
    public string PathFor(string sourceFile)
    {
        var full = Path.GetFullPath(sourceFile);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())));
        var stem = ChapterService.SanitizeTitle(Path.GetFileNameWithoutExtension(full));
        return Path.Combine(_dir, $"{stem}-{hash[..8].ToLowerInvariant()}.json");
    }

    public bool ExistsFor(string sourceFile) => File.Exists(PathFor(sourceFile));

    /// <summary>Grava a grade atual do episódio (sobrescreve).</summary>
    public void Save(string sourceFile, IReadOnlyList<ChapterEditRecord> chapters)
    {
        Directory.CreateDirectory(_dir);
        var payload = new
        {
            SourceFile = Path.GetFullPath(sourceFile),
            SavedAt = DateTime.Now,
            Chapters = chapters,
        };
        File.WriteAllText(PathFor(sourceFile), JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>Lê a grade salva; null se não existe ou o JSON não pôde ser lido.</summary>
    public List<ChapterEditRecord>? Load(string sourceFile)
    {
        try
        {
            var path = PathFor(sourceFile);
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Chapters", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<ChapterEditRecord>();
            foreach (var c in arr.EnumerateArray())
            {
                list.Add(new ChapterEditRecord(
                    c.TryGetProperty("Number", out var n) && n.TryGetInt32(out var nv) ? nv : 0,
                    c.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "",
                    c.TryGetProperty("StartSeconds", out var s) && s.TryGetDouble(out var sv) ? sv : 0,
                    c.TryGetProperty("EndSeconds", out var e) && e.TryGetDouble(out var ev) ? ev : 0,
                    c.TryGetProperty("Class", out var cl) ? cl.GetString() ?? "Episode" : "Episode",
                    c.TryGetProperty("TargetKbps", out var k) && k.TryGetInt32(out var kv) ? kv : 0,
                    !c.TryGetProperty("Include", out var inc) || inc.ValueKind != JsonValueKind.False,
                    c.TryGetProperty("IsTemporary", out var tmp) && tmp.ValueKind == JsonValueKind.True,
                    c.TryGetProperty("Preset", out var pr) && pr.TryGetInt32(out var pv) ? pv : 0,
                    c.TryGetProperty("Cq", out var cq) && cq.TryGetInt32(out var cv) ? cv : 0));
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // arquivo corrompido/meio-escrito: cai no comportamento "sem edição" em vez de derrubar a tela
            return null;
        }
    }

    /// <summary>Apaga o arquivo de edição (Resetar Capítulos). true se apagou algo.</summary>
    public bool Delete(string sourceFile)
    {
        var path = PathFor(sourceFile);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Converte o texto da classe gravado de volta ao enum (legado/estranho vira Episode).</summary>
    public static BitrateClass ParseClass(string? value) =>
        Enum.TryParse<BitrateClass>(value, ignoreCase: true, out var c) ? c : BitrateClass.Episode;

    /// <summary>Classe como texto para gravar no JSON (invariante, independe de cultura).</summary>
    public static string FormatClass(BitrateClass cls) =>
        cls.ToString();
}
