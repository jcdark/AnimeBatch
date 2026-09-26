# Lab — AV1 Híbrido CPU/GPU com IA

Laboratório de benchmark do projeto de encoder AV1 híbrido (branch `feature/av1-hybrid`).
Todo trabalho de otimização (IA de poda de candidatos no RDO, kernels CUDA no fork do
SVT-AV1) só avança com comparação automática contra o baseline congelado aqui.

## Estrutura

```
lab/
├── README.md              este arquivo
├── clips.json             manifest PÚBLICO do clipset (ids + metadados técnicos)
├── clips.local.json       manifest LOCAL com caminhos de origem (gitignored)
├── clips/                 clipes cortados (gitignored)
├── out/                   saídas de encode dos testes (gitignored)
├── tmp/                   temporários (y4m, stat files) (gitignored)
├── resultados/            relatórios de cada rodada (versionado)
├── baselines/             baselines CONGELADOS (versionado — referência de todas as comparações)
├── dataset/               dumps de decisões do fork (gitignored — GBs)
└── tools/
    ├── cut-clips.ps1      corta o clipset dos originais (diversidade por série/filme, seed fixa)
    ├── measure-vmaf.ps1   VMAF/SSIM/PSNR via libvmaf (ffmpeg de tools\)
    ├── run-baseline.ps1   matriz de encodes + métricas + relatório
    ├── verify-fork-parity.ps1  gate 3 vias: pristine v4.2.0 == fork dump=0 == fork dump=1 (lp=1)
    └── gen-dataset.ps1    Fase 2: gera dumps de decisões (clipes × pontos de taxa)

## Fase 2 — fork instrumentado do SVT-AV1 (decisões para a IA)

Fork: `E:\av1-refs\svt-av1` branch `animebatch-hybrid` (v4.2.0), pristine de comparação em
`E:\av1-refs\svt-av1-pristine` (worktree limpo). Build: `E:\av1-refs\build-fork.bat` (MSVC 18
BuildTools + NASM portátil em `E:\av1-refs\build-tools\nasm-2.16.03`).

- **Dump de decisões** (`Source/Lib/Codec/sva_dump.{c,h}`): por CU codificado no `md_encode_block`,
  grava vencedor (classe, modo, slice, qp, pd_pass, shape, skip, bsize, origem, MV, ref0, rd_cost)
  + lista dos candidatos do loop final com o rd_cost de cada um. Envolvente `SVA_DUMP=1`,
  `SVA_DUMP_DIR`, `SVA_DUMP_LIST_EVERY=N` (amostragem das listas; vencedor sempre gravado).
  Um arquivo por contexto MD (`sva_dump_<ptr>.bin`), formato little-endian: header 12B
  (v2, CU=37B, cand=20B), frame 16B, CU 37B, candidato 20B.
- **Gate de paridade** (`verify-fork-parity.ps1`): pristine == fork(dump=0) == fork(dump=1)
  byte a byte — PASSED em clip01 p6 500k. Prova que o dump não muda decisão (regra do plano).
- **Nondeterminismo (importante)**: o SVT-AV1 4.2.0 com `--lp > 1` NÃO é determinístico
  run-a-run (verificado no stock MSYS2/gcc E no build MSVC); com `--lp 1` é determinístico.
  Comparação de streams byte a byte só faz sentido em lp=1; benchmarks (Fase 4+) precisam
  medir VMAF/tempo com repetições, não hashes.
- **Gerador de dataset** (`gen-dataset.ps1`): CQ 1-pass (cq20/28/36) por clipe; ~60 MB/run
  com ListEvery=8 (dataset completo dos 9 clipes ≈ 1.6 GB em `lab\dataset\`).
```

## Fluxo

```powershell
# 1. Clipset (uma vez; seed 42 garante reprodutibilidade)
powershell -ExecutionPolicy Bypass -File lab\tools\cut-clips.ps1 -SourceRoot F:\Dublados

# 2. Rodada de benchmark (baseline = SVT p6 @500k 2-pass, mesmos --keyint 240 --scd 0 do app)
powershell -ExecutionPolicy Bypass -File lab\tools\run-baseline.ps1 -Label baseline-v1 -Freeze

# 3. Depois de cada mudança no encoder, rodar de novo SEM -Freeze e comparar
powershell -ExecutionPolicy Bypass -File lab\tools\run-baseline.ps1 -Label svt-fork-dump
```

## Engines suportadas

| engine | comando | paralelo com o app |
|---|---|---|
| `svt_p6_500k_2pass` | `SvtAv1EncApp -i clip.y4m --keyint 240 --scd 0 --preset 6 --rc 1 --tbr 500 --passes 2` | string `-v` idêntica à do motor av1an |
| `nvenc_p7_500k` | `ffmpeg -c:v av1_nvenc -preset p7 -rc vbr -b:v 500k -maxrate 1200k -bufsize 2400k` | derivação VBR do app (maxrate 2,4× / bufsize 4,8×) |

## Decisões registradas

- **Clipes**: 9 cortes de 45s sem áudio (o baseline mede vídeo), 1 de Filmes + 8 de Séries
  (1 por pasta = diversidade), offset 35% do arquivo (depois do OP), perda zero (x264 qp0).
- **Fonte dos clipes**: originais dublados (h264 SD, espelho sincronizado em `F:\Dublados`
  de `G:\Dublados`). Caminhos reais ficam só em `clips.local.json` (repo público).
- **Nível de benchmark**: encoder direto (sem av1an) — o fork substitui o encoder, o
  orquestrador é fixo; parallelismo por chunks é variável de outra camada.
- **Clipset 1080p**: produção encodeia 1080p APÓS o upscale; após o baseline v1 em SD,
  avaliar um clipset 1080p gerado pelo caminho de upscale do app (mesmo pipeline de produção).
- **NVENC**: pula sozinho com driver < 610 (máquina em 591.86 até 25/09/2026) — atualizar
  o driver NVIDIA destrava o braço NVENC do baseline.
- **Métricas**: VMAF (modelo padrão v0.6.1), SSIM float, PSNR, tempo de parede (2 passadas
  inclusas), fps efetivo = frames/tempo, kbps/size por ffprobe.
