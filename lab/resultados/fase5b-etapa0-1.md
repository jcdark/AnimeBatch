# Fase 5b — Etapa 0 e Etapa 1 (26/09/2026)

## Etapa 0 — Teto teórico (`SVA_ME_STATS=1`, commit do fork com o módulo de stats)

Encode de 3 min (Suzume @4500s, 1080p 23.976, 4317 frames), p6 @500k 2-pass,
threading de produção (sem `--lp`), build CPU do fork:

| Métrica | Valor |
|---|---|
| Tempo total do encode | 58 s (13.4 ms/frame) |
| Chamadas ao `svt_sad_loop_kernel` | 13.875.312 (≈ 3213/frame nas 2 passadas) |
| Tempo dentro do kernel | 24,2% do encode (3,25 ms/frame) |

**GO**: a fatia está acima do corte de 10–15% — o teto absoluto de ganho (GPU de graça)
é ~24%, acima do critério de 10% ponta a ponta.

## Mix real (`SVA_ME_STATS=2`)

~1605 jobs/frame/passada, 110 combinações distintas. Dominado por blocos PEQUENOS com
buscas variadas: `32x16 @ 8x3` (18,7% das chamadas), `16x16 @ 16x16`, `32x32 @ 16x16`,
família `16x8 @ 16x4..14` e cauda com buscas largas (`80x70`, `384x3`, `8x285`).

## Etapa 1 — Protótipo de lote (`cuda-lab batch`, commit 1490d1e)

Mesma geometria de kernel da integração v1 (thread-por-posição, chave sad<<24|pos,
atomicMin por job = tie-break da 1ª posição row-major):

| Métrica | Valor |
|---|---|
| Jobs/quadro no protótipo | 1605 (proporções do mix real) |
| Posições / ops SAD por quadro | 561.368 / 109 M |
| Upload por quadro | 2,35 MB (src 0,56 + ref 1,79) |
| **Paridade CPU × GPU** | **OK exata (0/1605 jobs divergentes)** |
| GPU lote completo (H2D+kernel+D2H, memcpy sincrono pageable) | **0,80–0,85 ms** |
| GPU kernel só | 0,23 ms |
| CPU do encoder (mesma busca, AVX2, todos os núcleos) | 3,25 ms |

**Gate da Etapa 1 (≥3× vs CPU no mix): PASSA com 4,1×** — mesmo pagando upload+download
sincronos por quadro. Com pinned/async seria melhor ainda.

## Parecer técnico

O batching por quadro é VIÁVEL: mesmo com transfers ingênuos, o lote GPU usa 0,8 ms do
orçamento de 3,25 ms/frame que a ME gasta hoje — sobra ~18% de ganho ponta-a-ponta
potencial (teto 24,2%). Pré-requisito para o número real: Etapa 2 (reestruturar a HME do
fork em passes por nível), com os gates de paridade sha256 + benchmark do harness congelado.
Riscos restantes: byte-exato no encoder real (k=2 line-skipping, prehme/pd), VRAM/contenção
com N chunks do av1an (projeção: tranquilo, dezenas de MB por processo).

## Pendências conhecidas do protótipo

- Timing usa `cudaMemcpy` sincrono pageable (pinned/async melhora o H2D de 2,35 MB).
- k=1 apenas no lab (o kernel do fork já suporta k=2; o gate real é no encoder).
- Probes de diagnóstico deixados de fora na versão commitada; a investigação do
  "0xC0000142" virou um bug real de heap (escalonamento dos jobs) — corrigido em 1490d1e.
