namespace AnimeBatch.Core.Models;

/// <summary>Palavra-chave de classificação de capítulos por título (substring, case-insensitive).</summary>
public class KeywordRule
{
    public int Id { get; set; }

    public KeywordCategory Category { get; set; }

    public string Word { get; set; } = "";
}
