# Gate de paridade CUDA v1 (Fase 5)

Build: SVA_ENABLE_CUDA (build-hybrid-cuda), clip clip01.mkv (45s 1080p), p6 @500k 2-pass, --lp 1

| Rodada | SVA_CUDA | Tempo (s) | sha256 (16) | GPU util max |
|--------|----------|-----------|-------------|--------------|
| A cpu  | -        | 81.3 | 81416519e5e0adbe | - |
| B gpu  | 1        | 1197.4 | 81416519e5e0adbe | 51% |
| B2 gpu | 1        | 1197.5 | 81416519e5e0adbe | - |

Gate A==B: OK (saida byte-exata)
Gate B==B2: OK (determinismo)

## Análise

- Kernel bit-exato em condições reais de encode (não só no cuda-lab): os três braços
  produziram o mesmo sha256, e B/B2 mostram determinismo run-a-run em --lp 1.
- A lentidão (14,7x) é estrutural do design por-chamada: cudaMemcpy2D pageable + sync +
  lock global por busca, com centenas de buscas pequenas por frame. Nem melhorias de
  constante (pinned memory, CUDA Graphs) mudam a ordem de grandeza — o caminho aceitável é
  batching por frame.
