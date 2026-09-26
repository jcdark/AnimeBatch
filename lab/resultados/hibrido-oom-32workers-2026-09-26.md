# OOM do motor Híbrido IA em produção (26/09/2026) — diagnóstico e correção

## Sintoma relatado pelo dono (V0.54)
- Iruma-kun S04E21 com "AV1 Híbrido IA 10bits" (preset 4, sequencial): rodapé morto
  (FPS 0.0, Speed 0.0x, Elapsed 00:00, 0%) durante ~20 min — "parece parado".
- Depois o item foi a ERRO: "av1an falhou (código 1)" com `SvtMalloc[fatal]: allocate
  memory failed` (pic_buffer_desc.c) no rabo do erro (data\crash.log 17:51).

## Causa raiz (matéria medida, não teoria)
O app mandava `-w 32` ao av1an (ProcessorCount / partes paralelas). Cada processo
SvtAv1EncApp (1080p10, 2-pass VBR, lookahead 42) segura **~2,0–2,4 GB de RAM** com 32
instâncias simultâneas = 60–77 GB — a máquina do dono tem 48 GB → esgota commit →
`allocate memory failed` em cascata + 0xc0000005 (Event Viewer, tudo em SvtAv1Enc.dll).

### Matriz de reprodução (clipe de 3 min do episódio, pré-corte x264 qp0 10-bit idêntico ao do app, 32 workers)
| Braço | Binário | IA (SVA_AI) | Preset | Resultado |
|---|---|---|---|---|
| A | fork (dist 0.54) | safe | 4 | OK 37/37 chunks, **pico 2,49 GB/proc**, 154 s |
| B | fork (dist 0.54) | off | 4 | OK 37/37, **pico 2,57 GB/proc**, 28 mallocs benignos, 155 s |
| C | stock 4.2.0 (tools\svt-av1) | — | 4 | OK 37/37, **pico 2,44 GB/proc**, 153 s |
| D | stock 4.2.0 (tools\svt-av1) | — | 6 | OK 37/37, **pico 2,05 GB/proc**, 106 s |

Conclusões:
1. **O inchaço NÃO é do fork nem da poda IA** — stock 4.2.0 se comporta igual (A=B=C; B vs
   C diferem no bitstream só pelo toolchain MSVC vs MSYS2; a paridade do fork foi provada
   contra upstream no MESMO compilador nos gates da Fase 5b).
2. P6 é ~20% mais leve que p4, mas as duas estouram 48 GB com 32 workers. Os codecs
   antigos sobreviviam por margem apertada (commit < working set); o p4 do híbrido
   empurrou além do limite.
3. Segundo bug real (exibição): o pré-corte rodava SEM `-progress pipe:1` → o
   RunFfmpegAsync não emite relato nenhum durante o corte (que levou 16 min nessa parte)
   → rodapé congelado em 0%. O watchdog de stall também podia matar cortes > 10 min.

## Correção (V0.55, tudo no APP — fork e encodes intocados)
- `EncodeService.Av1anInternalWorkers(processors, parallel, totalRam)`: teto de workers
  internos por RAM física (80% / 2,75 GB por processo, orçamento do pior caso). Na
  máquina do dono: 32 → 13 workers sequenciais. RAM desconhecida = comportamento antigo.
- `EncodeService.BuildPrecutArgs`: pré-corte ganha `-progress pipe:1 -nostats` → rodapé
  vivo durante o corte e watchdog alimentado.
- Testes: `Av1an_internal_workers_limita_pela_ram_fisica`,
  `Precut_args_tem_progress_e_parametros_do_corte`. 229/230 verdes (1 skip NVENC driver).
