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

---

# Fase 5b — Etapa 2 (HME por lotes no fork) — RESULTADO FINAL (26/09)

Implementada por agente especialista em 3 incrementos + 1 ciclo de otimização:

| Marco | Commit | Gate paridade | Benchmark (clip01, p6 2-pass) |
|---|---|---|---|
| Inc 2 — executor + lote L1 por segmento | 56b54709b + 44099102f | VERDE 4/4 (1ª arquitetura de fases validada) | 87,8s vs 80,5s CPU (−9%) |
| Inc 3 — L0 em duas fases (L0a/L0b) | 937602a1e | VERDE 4/4 (1ª tentativa) | 91,0s (−13%) |
| Inc 3b — row-memcpy + snapshot flat (1 memcpy span) | 002de2b92 + ffc9832ed (revert L0) | VERDE 4/4 (estado final) | GPU −11,5% em lp1; **−89% em threading de produção** (25 threads × mutex) |

## Diagnóstico final (o valor do experimento)

- A maquinaria de fases foi levada a custo ~zero (braço fallback-CPU do lote: 89,7s →
  79,9s ≈ per-call). O gargalo remanescente é 100% o modelo de dados: **upload por job**
  (as SAs do L0 são enormes — milhares de jobs × 7–15 KB) + launches serializados entre
  segmentos. Com 1 ref, o L0b nem gera jobs e o L0a são só ~2040 jobs/picture.
- Bug real de paridade encontrado e corrigido no caminho (snapshot uint16 vs uint32 —
  vazamento de estado entre b64s), além de um zz_sad sujo detectado pelo próprio gate
  (falso-rápido, corrigido).
- Ferramenta permanente: `SVA_BATCH_DBG=1/2` (hash FNV por b64 do estado L1/ready/final).

## Veredito (critério do plano: ≥10% de ganho ou encerrar)

**Fase 5b ENCERRADA sem ganho**: o lote GPU não atinge o critério com o modelo
"upload por job"; a única alavanca restante é o redesenho "refs residentes em device +
overlap async" — ciclo novo, custo de semanas, retorno incerto.

## Estado final (produção intacta)

- Fork em `002de2b92`: lote L1-only sob `SVA_CUDA=1 + SVA_CUDA_BATCH=1` (env OFF por
  padrão; byte-exato; fallback CPU; **NÃO usar o env em produção** — contenção de mutex
  com 25 threads). Sem as envs, o encoder é byte-idêntico ao CPU puro (gate 4/4).
- L0 loteado está no histórico (937602a1e) para retomada futura com refs residentes.
