namespace AnimeBatch.Core.Models;

/// <summary>Uma parte (capítulo) de um job: um trecho do episódio com bitrate alvo próprio.</summary>
public class JobItem
{
    public int Id { get; set; }

    public int JobId { get; set; }

    public Job? Job { get; set; }

    /// <summary>Número exibido do capítulo (1-based, como visto na tela de episódios).</summary>
    public int Order { get; set; }

    public string Title { get; set; } = "";

    public double StartSeconds { get; set; }

    public double EndSeconds { get; set; }

    public BitrateClass Class { get; set; } = BitrateClass.Episode;

    public int TargetKbps { get; set; }

    public JobItemState State { get; set; } = JobItemState.Pending;

    /// <summary>Arquivo de saída da parte, preenchido ao concluir o encode.</summary>
    public string? OutputPath { get; set; }
}
