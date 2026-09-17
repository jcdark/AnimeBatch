using System.Text.Json;

namespace AnimeBatch.Core.Services;

public record LanguageInfo(string Code, string Name, string Author, string Contact, string Version, string FilePath);

/// <summary>
/// Sistema de tradução por arquivos JSON numa pasta (i18n\ ao lado do executável).
/// Formato de cada arquivo (ex.: pt-BR.json):
/// {
///   "code": "pt-BR", "name": "Português (Brasil)", "author": "Nome", "contact": "email",
///   "version": "1.0",
///   "translations": { "chave": "texto", ... }
/// }
/// Pra adicionar um idioma novo: crie o JSON na pasta — ele aparece no seletor.
/// Fallback: idioma atual → pt-BR → en-US → a própria chave.
/// </summary>
public class Localization
{
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageInfo> _languages = [];

    public IReadOnlyList<LanguageInfo> Languages => _languages;
    public string CurrentCode { get; private set; } = "pt-BR";
    public string CurrentName => _languages.FirstOrDefault(l => l.Code == CurrentCode)?.Name ?? CurrentCode;

    /// <summary>Varre a pasta carregando todos os *.json válidos.</summary>
    public void Load(string folder)
    {
        _catalogs.Clear();
        _languages.Clear();

        if (!Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                string S(string prop) =>
                    root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                        ? el.GetString() ?? ""
                        : "";

                var code = S("code");
                if (code.Length == 0)
                    code = Path.GetFileNameWithoutExtension(file);

                var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in tr.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String)
                            translations[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                _catalogs[code] = translations;
                _languages.Add(new LanguageInfo(code, S("name") is { Length: > 0 } n ? n : code, S("author"), S("contact"), S("version"), file));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // arquivo de tradução inválido — ignora e segue (não derruba o app)
            }
        }
    }

    public void SetLanguage(string code)
    {
        if (_catalogs.ContainsKey(code) || _languages.Any(l => l.Code == code))
            CurrentCode = code;
    }

    /// <summary>Traduz a chave com fallback: atual → pt-BR → en-US → a própria chave.</summary>
    public string T(string key, params object[] args)
    {
        var text = Lookup(CurrentCode, key)
                   ?? Lookup("pt-BR", key)
                   ?? Lookup("en-US", key)
                   ?? key;
        return args.Length > 0 ? string.Format(text, args) : text;
    }

    private string? Lookup(string code, string key) =>
        _catalogs.TryGetValue(code, out var catalog) && catalog.TryGetValue(key, out var value) ? value : null;
}
