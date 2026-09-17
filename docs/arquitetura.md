# Arquitetura — AnimeBatch

Visão curta do pipeline e das decisões que não são óbvias pelo código. Detalhes de gotchas
ficam no `AGENTS.md` e nos comentários inline (que carregam o "porquê" com evidência).

## Projetos

| Projeto | Papel |
|---|---|
| `AnimeBatch.App` | WinUI 3: 6 páginas + composição (`AppServices`), sem lógica de fila |
| `AnimeBatch.Core` | Tudo que roda sem UI: modelos/EF Core, serviços de processo, `Core.Queueing` (fila) |
| `AnimeBatch.Tests` | xUnit: unit (builders de args, parsers) + integração real (ffmpeg/ncnn/ONNX/SQLite); pula sem `tools\` |

`AppServices` é a composition root: monta `ToolsLocator`, repositórios e o `QueueRunner`
injetando implementações reais (o Core não conhece UI; som de conclusão e crash log entram
como delegates em `QueueRunnerDeps`).

## Pipeline de um job

```
probe (ffprobe)
  → Fase 1 (opcional, UpscaleMode.WithEncode): upscale por parte → intermediário lossless ups_NN.mkv
  → Fase 2: encode por parte (sequencial ou pool de workers NVENC, -gpu N)
  → merge (mkvmerge append "+", capítulos cumulativos OGM; Critical NÃO vira capítulo)
  → ConversionRecord + som + auto-remoção opcional
```

- Fontes em `G:\Dublados` (só `SourcePath` no job); trabalho em `{saída}\AnimeBatch\{base}\`;
  final em `{saída}\{base}.mkv`.
- Áudio da 1ª trilha sempre AAC 160k (stream-copy quebra o corte `-ss`/`-t`).
- VFR: extração força `-fps_mode cfr -r fps` (senão o áudio desliza).
- `-progress pipe:1` em TODAS as passadas (inclusive a 1ª do SVT 2-pass): é o sinal de vida
  do watchdog de stall (`queue.stallMinutes`, default 10 min → kill + parte vira Error).

## Upscale (2 motores)

- **ONNX/DirectML** (`"onnx"`, atual): ffmpeg despeja rawvideo rgb24 na stdout → tensor NCHW
  → `InferenceSession` (AnimeJaNai 2x; 4x = 2 passes; 3x = 2x+lanczos, decisão medida) →
  stdin de outro ffmpeg que monta segmento x264. **Sessão DML é POR WORKER** — `Run`
  concorrente na mesma sessão deadlocka (cache por modelo/placa/slot).
- **ncnn/Vulkan** (legado): chunks em PNG, `realcugan`/`realesrgan`, 2 workers por GPU
  principal, progresso por contagem de PNGs.

## Fila (`Core.Queueing.QueueRunner`)

- Um job por vez; pega o menor `Order` pendente; erro em parte/job → `Error` + mensagem
  (200 chars) e a fila **segue** pro próximo.
- **Pausar** → job `Paused` (parte volta `Pending`); **Parar** → job `Pending`.
- `RunAsync` reseta órfãos: `Error`, `Paused` e `Running` (crash/kill) voltam a processar.
- Retomada em todos os níveis: parte `Done` com arquivo presente pula o encode;
  intermediário de upscale presente pula o estágio; final presente pula o "Apenas upscaling".
- Escritas de estado de item serializadas por `_dbGate` (SemaphoreSlim) — encode paralelo
  dispara N writers e SQLite não gosta de concorrência.

## GPUs — TRÊS espaços de índice (não confundir)

| Motor | Índice vem de | Quem resolve |
|---|---|---|
| Encode NVENC (`-gpu N`) | nvidia-smi/CUDA | `NvencGpuProbe` |
| Upscale ncnn (`-g N`) | Vulkan (pergunta ao próprio ncnn) | `VulkanGpuProbe` / `GpuSelector.ResolveNcnnAsync` |
| Upscale ONNX (device id DML) | DXGI | `DxgiGpuProbe` (+ fallback `DmlDeviceCalibration` por delta de VRAM) |

A tela Hardware configura workers POR NOME de placa (`setting hardware.gpus`); `GpuSelector`
traduz para o espaço certo por motor. Configuração explícita é decisão final: pool vazio
(todas em 0 workers) = erro claro, sem cair no automático.

## Configurações (tabela Setting)

`tmdb.apikey`, `app.language`, `output.dir`, `source.dir`, `queue.autoRemove`,
`queue.stallMinutes`, `hardware.gpus`, `upscale.gpus` (legado), `tools.extraDirs`,
`encode.cfg.{codec}` (JSON; deserialização tolerante preserva defaults de chaves novas —
configs antigas continuam funcionando).

## Resiliência de processo

`ProcessRunner` centraliza start com pipes, captura com timeout (nvidia-smi etc.), kill de
árvore e truncamento de stderr. `ProbeService` tem timeout rígido de 60s; encode tem watchdog
de stall; tools extras via `tools.extraDirs` (mais o legado `D:\ffmpeg-*` da máquina do dono).
