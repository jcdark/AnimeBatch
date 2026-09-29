# Plano — Fase 5b: ME por batching de quadro na GPU (verificação de ganho)

Data: 26/09/2026 · Branch do app: `feature/av1-hybrid` · Branch do fork: `animebatch-hybrid`

## Decisão do dono que este plano respeita

- **Produção segue 100% CPU**: a estimativa de movimento (ME) continua no CPU na 0.54 e
  em qualquer release futura até que este experimento PROVE ganho e o dono aprove embarcar.
- O executável atual em `tools\svt-av1-hybrid\` (Fase 4, CPU) NÃO é substituído por nada
  deste plano.
- Este plano é um EXPERIMENTO no fork + laboratório, com critérios de saída antecipada em
  cada etapa. O resultado "não deu ganho" fecha a Fase 5 definitivamente, sem custo grande.

## Pergunta a responder

O batching por quadro (mandar o trabalho de ME do quadro INTEIRO em poucos disparos de
kernel, em vez de uma chamada por busca) gera **ganho real de tempo ponta a ponta** no
encode congelado (SVT p6 @500k 2-pass), mantendo a saída byte-exata?

- Métrica de sucesso: ≥ **10% de redução** no tempo de parede no harness congelado
  (`run-baseline.ps1`), em ≥ 3 clipes, com sha256 do `.ivf` idêntico ao braço CPU.
- Qualidade: não precisa de VMAF — a exigência de byte-exato implica qualidade idêntica
  por definição.

## Hipótese (baseada na v1)

- O gate v1 (`resultados\cuda-parity-v1.md`) provou: kernel bit-exato, mas 14,7× MAIS
  LENTO por causa do pedágio por chamada (~0,7 ms de cópia PCIe + sync + lock por busca;
  o cálculo em si é ~0,02 ms) — centenas de buscas pequenas por quadro.
- O cuda-lab já provou 10× de ganho no kernel quando o quadro vai em LOTE (transfers
  amortizados). O batching ataca exatamente o pedágio.

## Guardrails

1. `SVA_CUDA` continua default-OFF; sem a variável, o comportamento é idêntico ao CPU
   (já provado: o braço A do gate v1 É o binário com suporte CUDA rodando em modo CPU).
2. Subpel, RDO, rate control, GOP, filtros: sempre CPU (plano original, seções 6/12/13/17).
3. `svt_pme_sad_loop_kernel` (product_coding_loop) fica FORA — tem early-exit e semântica
   de custo de MV que quebram a exigência de byte-exato; documentado como trabalho futuro.
4. Todo gate roda em `--lp 1` (determinismo); benchmark final usa o harness congelado.

## Etapa 0 — Medir o TETO teórico (1–2 dias) · saída antecipada barata

No fork, adicionar contadores env-gated (`SVA_ME_STATS=1`): nº de chamadas e tempo total
acumulado dentro de `svt_sad_loop_kernel` (rdtsc; relatório no fechamento do encoder, no
stderr, sem tocar bitstream — mesmo estilo do SVA_DUMP).

- Se o kernel responde por **< 10–15% do tempo total** do encode p6, o ganho máximo
  possível (GPU de graça) é menor que o critério de sucesso ⇒ **FECHAR a Fase 5 aqui**,
  sem escrever o batching (economiza as semanas das etapas 2–3).
- Se ≥ 15%, seguir para a Etapa 1 com o teto em mãos (ex.: ME = 20% do tempo ⇒ ganho
  máximo possível = 20%).

## Etapa 1 — Protótipo de batch no cuda-lab (2–4 dias)

Generalizar o kernel do cuda-lab para uma LISTA de jobs (por job: ponteiro do bloco fonte,
retângulo da área de busca, dims, saw/sah/skip), um disparo por lote, results idênticos.

- Validar paridade SAD bit-exata contra o CPU no mix REAL de um quadro 1080p
  (65 SBs × níveis × refs) e medir: batched GPU vs CPU AVX2 no mesmo mix.
- Go/no-go: **≥ 3× mais rápido que o CPU no mix** (número que, multiplicado pela fatia da
  Etapa 0, sustenta os 10% ponta a ponta) E paridade exata. Senão, fechar.

## Etapa 2 — Batching dos níveis HME no fork (1–3 semanas)

Reestruturar o loop de HME (`motion_estimation.c`) de "por SB" para "por nível":

1. Nível 0 (sixteenth) de TODOS os SBs → 1 lote na GPU → resultados por SB.
2. Nível 1 (quarter) idem (cada SB depende só do resultado L0 DELE — paridade preservada).
3. Nível 2 (full-res) idem.

- pre-HME e PD seguem no CPU nesta fase (documentar).
- Caminho CPU atual permanece intacto como fallback (mesmo padrão da v1: sem SVA_CUDA=1,
  zero diferença).
- Riscos conhecidos: reorganizar buffers por SB (b64 src de cada nível vira coleta em
  lote); ordem de execução muda (nível-a-nível em vez de SB-a-SB) mas o RESULTADO por SB
  não pode mudar — o gate byte-exato é quem garante.

## Etapa 3 — Gates + benchmark (2–3 dias)

1. `verify-cuda-parity.ps1` (A==B==B2 byte-exato) no clip01.
2. `run-baseline.ps1` em ≥ 3 clipes (incluir 1 de movimento pesado), SVA_CUDA off vs on,
   comparando tempo de parede — critério dos 10%.
3. Teste de produção: caminho av1an do app (N processos de chunk compartilhando a GPU) —
   medir VRAM por processo e se o ganho sobrevive ao compartilhamento.
4. Estabilidade: 1 encode longo (episódio 24 min) sem leak de VRAM (contexto por processo
   cresce buffers sob demanda — confirmar teto de memória).

## Etapa 4 — Decisão de embarcar (só com o dono)

Se tudo passar: o binário CUDA-capable pode substituir o de `tools\svt-av1-hybrid\` com
segurança (sem a env var é byte-idêntico ao CPU), e o ganho fica disponível como OPT-IN
(ex.: setting "usar GPU na busca de movimento" na aba Encodes, default desligado).
Se qualquer gate falhar: Fase 5 encerrada com a conclusão documentada, produção intacta.

## Fora de escopo deste plano

- PME/RDO na GPU (semântica incompatível com byte-exato hoje).
- Migrar predição/transform/quant/filtros (seria um novo encoder — seção 17 do plano
  original; só reavaliar se a Etapa 3 der ganho grande e o dono quiser continuar).
- Alterar encodes congelados (preset/p6/500k/2-pass intocados).

## Cronograma resumido

| Etapa | Duração | Fecha o experimento se... |
|-------|---------|---------------------------|
| 0. Teto (contadores) | 1–2 dias | ME < 10–15% do tempo do encode |
| 1. Batch no lab | 2–4 dias | < 3× vs CPU no mix, ou sem paridade |
| 2. Fork | 1–3 semanas | (não tem saída — termina e vai pro gate) |
| 3. Gates + bench | 2–3 dias | < 10% ponta a ponta, ou problema no av1an |
| 4. Decisão | — | dono aprova ou arquiva |
