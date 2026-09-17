namespace AnimeBatch.Core.Models;

public class Series
{
    public int Id { get; set; }

    /// <summary>Nome da série como digitado (exibição).</summary>
    public string Name { get; set; } = "";

    /// <summary>Nome normalizado usado no match com arquivos de episódio (minúsculas, sem " - SXXEXX").</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>Bitrate alvo (kbps) de capítulos normais (episódio).</summary>
    public int EpisodeKbps { get; set; }

    /// <summary>Bitrate alvo (kbps) de capítulos de abertura.</summary>
    public int OpeningKbps { get; set; }

    /// <summary>Bitrate alvo (kbps) de capítulos de encerramento.</summary>
    public int EndingKbps { get; set; }

    // ---- Integração TMDB (opcional) ----

    /// <summary>ID da série no TMDB (themoviedb.org); null = não vinculado.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Caminho do poster no TMDB (ex.: "/abc.jpg"); null = sem imagem.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Sinopse no idioma pt-BR.</summary>
    public string? Overview { get; set; }
}
