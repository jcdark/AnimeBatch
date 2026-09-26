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

    /// <summary>Preset do encode só desta parte (null = usa o preset da config do codec).
    /// Sobrepõe o --preset/-preset do vídeo; o clamp por engine (SVT 1–13, NVENC 1–7)
    /// continua acontecendo no EncodeService.</summary>
    public int? Preset { get; set; }

    /// <summary>CQ/quality desta parte (null = usa o da config do codec). Só tem efeito
    /// quando o codec está em Qualidade Constante — em bitrate médio quem manda é TargetKbps.</summary>
    public int? Cq { get; set; }

    public JobItemState State { get; set; } = JobItemState.Pending;

    /// <summary>Arquivo de saída da parte, preenchido ao concluir o encode.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Capítulo temporário: é encodeado como qualquer parte (divide o vídeo para
    /// bitrate próprio) mas NÃO vira capítulo no arquivo final — o merge o trata como
    /// Critical (conteúdo entra, entrada de capítulo não), como se não existisse no fim.</summary>
    public bool IsTemporary { get; set; }

    /// <summary>QC de qualidade opcional (libvmaf fonte×parte, rodada após o encode quando a
    /// setting queue.qualityCheck está ligada). Null = não medida. A referência é o trecho da
    /// ORIGEM (com upscale, é o intermediário ampliado — isola a qualidade do ENCODE).</summary>
    public double? QualityVmaf { get; set; }

    /// <summary>SSIM float da QC (null = não medida).</summary>
    public double? QualitySsim { get; set; }

    /// <summary>PSNR (plano Y quando disponível) da QC (null = não medida).</summary>
    public double? QualityPsnr { get; set; }
}
