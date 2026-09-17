namespace AnimeBatch.Core.Models;

/// <summary>
/// Histórico de um arquivo convertido: gravado quando a conversão de um episódio conclui,
/// para exibir na tela de Séries o que já foi gerado.
/// </summary>
public class ConversionRecord
{
    public int Id { get; set; }

    /// <summary>Série vinculada, quando identificada.</summary>
    public int? SeriesId { get; set; }
    public string SeriesName { get; set; } = "";

    public DateTime ConvertedAt { get; set; } = DateTime.Now;

    /// <summary>Nome do arquivo final produzido.</summary>
    public string FileName { get; set; } = "";

    public double DurationSeconds { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Caminho completo da saída, para reabrir/localizar depois.</summary>
    public string OutputPath { get; set; } = "";
}
