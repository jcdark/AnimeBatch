namespace AnimeBatch.Core.Models;

/// <summary>
/// Um episódio na fila. A ordem de processamento segue <see cref="Order"/> (reordenável na UI
/// enquanto o job estiver pendente) e sobrevive a reinícios do aplicativo.
/// </summary>
public class Job
{
    public int Id { get; set; }

    /// <summary>Posição na fila; o menor valor pendente executa primeiro.</summary>
    public int Order { get; set; }

    /// <summary>Caminho absoluto do episódio de origem.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Snapshot do nome da série no momento do enfileiramento.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Snapshot dos bitrates efetivos (kbps) no momento do enfileiramento.</summary>
    public int EpisodeKbps { get; set; }
    public int OpeningKbps { get; set; }
    public int EndingKbps { get; set; }

    public JobState State { get; set; } = JobState.Pending;

    /// <summary>Motivo do erro (preenchido quando State = Error; limpo ao voltar pra fila).</summary>
    public string? ErrorMessage { get; set; }

    // ---- Upscale opcional (aplicado antes do encode) ----

    public UpscaleMode UpscaleMode { get; set; } = UpscaleMode.None;

    /// <summary>"realcugan" ou "realesrgan".</summary>
    public string? UpscaleModel { get; set; }

    /// <summary>Altura alvo em pixels (720/1080/1440/2160).</summary>
    public int? UpscaleTargetHeight { get; set; }

    /// <summary>Codec do encode: "svt_av1", "svt_av1_10bit", "nvenc_av1" ou "nvenc_av1_10bit" (VideoCodecOptions).</summary>
    public string? VideoCodec { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<JobItem> Items { get; set; } = new();
}
