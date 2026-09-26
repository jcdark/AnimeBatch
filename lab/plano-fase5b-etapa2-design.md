# Fase 5b / Etapa 2 — Design da HME por lotes (nivel-por-lote no fork)

Data: 26/09/2026 · Status: EM IMPLEMENTAÇÃO · Autorização do dono: SIM ("Autorizo a etapa 2")

## Fonte da verdade

Mapa completo do fluxo HME levantado pelo agente especialista (arquivo:linha em cada
ponto), resumido aqui. Fork: `E:\av1-refs\svt-av1`, branch `animebatch-hybrid`.

## Fatos que definem o design (p6, open-loop, produção)

1. **Árvore**: `svt_aom_motion_estimation_kernel_iter` (me_process.c:94) → loop b64 do
   SEGMENTO (me_process.c:165-277) → `svt_aom_motion_estimation_b64` (motion_estimation.c:2889)
   → `hme_b64` (:2249) = init_zz_sad → prehme_b64 → hme_level0_b64 → hme_level1_b64 →
   (L2 off no p6) → set_final_search_centre_sb → (de volta ao b64:) prune → integer ME →
   candidates → distortion → GM-count.
2. **Paralelismo**: pool de N threads ME (20/25); picture fatiada em SEGMENTOS; cada
   tarefa PAME = 1 segmento exclusivo com 1 `MeContext` exclusivo → **lote natural por
   segmento** (lote global exigiria barreira — fora de escopo).
3. **No p6**: HME L0 (16×16 @ 1/16, SA por quadrante ~8..16 escalado por distância, cap 96)
   + L1 (32×32 @ 1/4, SA fixo 8×3), 2×2 quadrantes, loop de refs DENTRO de cada nível;
   `hme_search_method = SUB_SAD_SEARCH` ⇒ **k=2** (stride ×2, block_height>>1, SAD ×2 feito
   pelo chamador); prehme ligado (2 regiões 16×16 @ 1/16); `skip_search_line=0`; L2 off;
   early-exits dependentes de estágio anterior zerados no p6 mas VIVOS em outros presets.
4. **Dependência intra-SB no L0**: com `distance_based_hme_resizing` ativo, a SA do ref r
   depende do RESULTADO L0 de (list0, ref0) do MESMO SB (motion_estimation.c:1685-1686,
   mutação/restauração de `hme_l0_sa` em :1714-1717/:1858-1863) ⇒ **L0 em duas fases**:
   L0a = (li0,ri0) de todos os SBs; depois SA por ref calculada; L0b = refs restantes.
5. **Sem dependência entre SBs** (confirmado com evidência): L1(SB_i) = f(L0(SB_i));
   nenhum resultado de HME cruza SBs (init_me_hme_data zera tudo por b64; R2R FIX).
6. **Resultados vivem no MeContext reutilizado por SB** (quadrantes `x/y_hme_level*` +
   sads; vida útil = 1 SB) ⇒ o lote precisa de armazenamento PRÓPRIO por segmento
   (array de resultados indexado por b64 do segmento), escrito na fase de consumo.
7. MCTF (temporal_filtering), DG detector e mrp_detector têm fluxos PRÓPRIOS que usam o
   mesmo `svt_sad_loop_kernel` — ficam no caminho per-call (CPU) e não são tocados.

## Arquitetura escolhida

- **Executor explícito, não hook**: no modo lote, o ponteiro RTCD `svt_sad_loop_kernel`
  fica NO CPU (prehme, MCTF, DG e qualquer caminho não-lote continuam rápidos no CPU);
  os lotes L0a/L0b/L1 chamam um executor novo `sva_cuda_batch_search(jobs, n, out)`.
- **Env**: `SVA_CUDA=1` + `SVA_CUDA_BATCH=1` ativa o modo lote. `SVA_CUDA=1` sozinho =
  comportamento atual (per-call, mantido por compatibilidade). Sem env = CPU puro.
- **Job** (geometria idêntica ao kernel per-call e ao lab): `{src ptr, src_stride,
  src_stride_raw, ref ptr, ref_stride, bh, bw, saw, sah, skip}`; k = ref_stride/ssr
  derivado por job; resultado = chave empacotada `sad<<24 | pos` (tie-break 1ª posição
  row-major, igual ao kernel C). Buffer device por processo, cresce sob demanda, lock
  global serializando os launches dos segmentos (launch ~0,2-0,5 ms — muito abaixo do
  orçamento de 3,25 ms/frame que hoje é CPU).
- **Reestruturação do kernel ME (só no modo lote)** — o loop do segmento vira fases:
  - **Fase A** (por SB): prefetch/buffers/refs como hoje + `init_me_hme_data` +
    `init_zz_sad` + `prehme_b64` (CPU, per-SB, com early-exits preservados) + COLETA dos
    jobs L0a (quadrantes × (li0,ri0)) no array do segmento.
  - **GPU L0a** (1 launch).
  - **Fase A2** (por SB, barato): consome L0a no MeContext, calcula SA por ref dependente
    (mesma ordem/semântica de `get_hme_l0_search_area`), coleta jobs L0b.
  - **GPU L0b** (1 launch).
  - **Fase B** (por SB): escreve resultados L0 (centros/sads por quadrante), roda o
    replace-worst-quadrant do prehme + restore de `hme_l0_sa` como hoje, e COLETA jobs L1
    (centros vindos de L0).
  - **GPU L1** (1 launch).
  - **Fase C** (por SB): escreve resultados L1 e roda o restante INTOCADO:
    `set_final_search_centre_sb` → `hme_prune_ref_and_adjust_sr` → `integer_search_b64` →
    `me_prune_ref` → candidates/distortion/GM-count.
- **Fallbacks preservados**: flags de nível off, I-slice, pic skip, static bypass,
  early-exits (`me_early_exit_th`, `prev_me_stage_based_exit_th`, `do_ref`) são avaliados
  na COLETA com os mesmos valores de hoje (skip = job nem entra; resultado gravado como o
  atalho de hoje grava). MCTF/superres/refs escalados: ptrs efetivos copiados por SB.
- **Sem CUDA compilado**: modo lote não existe (stub), comportamento = build atual.

## Arquivos novos/alterados no fork

| Arquivo | Mudança |
|---|---|
| `Source/Lib/CUDA/sva_cuda_batch.{h,cu}` | executor de lote (kernel block-per-job 256 threads, atomicMin por job) |
| `Source/Lib/CUDA/CMakeLists.txt` | + sva_cuda_batch.cu |
| `Source/Lib/Codec/me_context.h` | struct do lote por segmento (jobs, resultados, índice) |
| `Source/Lib/Codec/motion_estimation.c` | coletores (`hme_b64_collect_l0/l1`) + consumidores; path atual intocado como fallback |
| `Source/Lib/Codec/me_process.c` | fases A/A2/B/C no loop do segmento quando lote ativo |

## Gates (regras do plano)

1. Build CPU (stub) inalterado; build CUDA OK.
2. **Paridade**: `verify-cuda-parity.ps1` com braço novo `SVA_CUDA=1 SVA_CUDA_BATCH=1` —
   sha256 do .ivf == braço CPU, em --lp 1 (clip01).
3. **Benchmark**: 3 clipes (clip01 + 1 movimento pesado + 1 série), tempo de parede
   batch ON vs OFF no build CUDA; critério ≥10%.
4. **Produção**: caminho av1an do app com N chunks (VRAM + ganho sob compartilhamento).
5. Estabilidade: episódio ~3 min contínuo sem leak (já o teste de 3min cobre; VRAM via
   nvidia-smi durante).

## Fora de escopo desta etapa

L2 (flag off no p6), PME (early-exit quebra byte-exato), prehme na GPU (fica per-SB CPU),
lote cross-segmento, MCTF/DG na GPU.
