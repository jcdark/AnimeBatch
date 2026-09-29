# Plano — Remoção do "AV1 Híbrido IA" do projeto (PROPOSTA, aguarda aprovação do dono)

Contexto (26/09): motor híbrido = fork do SVT-AV1 com poda de candidatos por prior
aprendido (Fases 2-4) + tentativa de kernels CUDA (Fase 5/5b, encerrada sem ganho).
Medições: qualidade estável, tempo -1..-4% (i.e., sem diferencial prático p/ o dono,
que confirmou "não vi nenhum resultado diferencial"). Decisão: remover do APP.

## Princípio
Remover só o código/ferramentas do APP. O histórico de pesquisa fica INTACTO
(fork E:\av1-refs\svt-av1, lab\ com gates e resultados, dists antigas).

## O que sai do app (inventário completo — grep "hybrid" em src/ + scripts/)
| # | Arquivo | O que remover |
|---|---|---|
| 1 | `Core/Models/Enums.cs` (~77-80) | 2 entradas do combo ("AV1 Híbrido IA (experimental)" 8/10bits) |
| 2 | `Core/Services/EncodeConfigRepository.cs` (~80) | "hybrid_av1", "hybrid_av1_10bit" do KnownCodes |
| 3 | `Core/Services/EncodeService.cs` | campos `_hybridSvtEncApp`/`_hybridPriorFile`, params de ctor, params `hybrid:` de EncodeWithAv1anAsync/RunAv1anAsync, despacho (~803-808), wiring PATH/SVA_AI_MODE/SVA_AI_PRIOR no RunAv1anAsync |
| 4 | `Core/Services/ToolsLocator.cs` (~47-53, 73, 76) | SvtHybridEncAppPath, HybridPriorFile |
| 5 | `App/Services/AppServices.cs` (~204-209) | wiring hybridSvtEncAppPath/hybridPriorFile |
| 6 | `App/Views/EncodesPage.xaml.cs` (~110) | condição `StartsWith("hybrid")` no gating |
| 7 | `Tests/ToolsHybridTests.cs` | arquivo inteiro |
| 8 | `scripts/make-release.ps1` (~55-59) | bloco de aviso svt-av1-hybrid |
| 9 | `tools\svt-av1-hybrid\` (repo) | pasta inteira (exe+dll+ai_prior.txt); dists NOVAS saem sem ela; dists antigas 0.54-0.58 ficam como estão |

## Proteções — o "sem impacto"
- **QC VMAF na fila (Fase 1)**: independente do híbrido — intocada.
- **Fixes recentes**: teto de workers por RAM, -progress do pré-corte, rodapé com fase,
  label do When done — todos no caminho av1an/geral, ficam.
- **Config antiga no banco** (`encode.cfg.hybrid_*`): fica órfã e inofensiva — a página
  de Encodes só lista KnownCodes; regra "config antiga continua funcionando" preservada.
- **Job residual com codec hybrid** (fila/banco antigo): manter em EncodePartAsync um
  despacho explícito que ERRA com mensagem clara ("codec removido — use AV1an 10bits"),
  em vez de cair silenciosamente em outro caminho. ~3 linhas + comentário.

## Passos (ordem de execução, ~1-2h, risco baixo)
1. Seguir na `feature/av1-hybrid` (o codec nasceu e morre nela); main intocada.
2. Remoção dos itens 1-8 + guard do job residual (item 3 inclui o despacho).
3. `dotnet test` suíte toda verde; `grep -ri hybrid src/` zerado (exceto guard).
4. `make-release.ps1` gera 0.59 SEM tools\svt-av1-hybrid; copiar animebatch.db da 0.58.
5. Verificação do pacote: combo de codecs sem as 2 entradas; smoke CLI do motor av1an
   (já coberto); smoke de app é do dono (padrão).
6. Commit único + memória do projeto atualizada.

## Ganho
Pacote ~5,7 MB menor, UI sem 2 opções experimentais sem diferencial, EncodeService
volta ao formato pré-Fase 6 (mais simples). Nada em produção muda de comportamento.
