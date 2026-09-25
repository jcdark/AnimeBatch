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
  → Fase 2: encode por parte (sequencial; pool NVENC por placa, -gpu N; ou pool CPU
    paralelo p/ SVT — cfg.ParallelWorkers 1–3, seletor da aba Encodes)
  → merge (mkvmerge append "+", capítulos cumulativos OGM; Critical NÃO vira capítulo)
  → ConversionRecord + som + auto-remoção opcional
```

- Fontes em `G:\Dublados` (só `SourcePath` no job); trabalho em `{saída}\AnimeBatch\{base}\`;
  final em `{saída}\{base}.mkv`.
- Áudio da 1ª trilha sempre AAC 160k (stream-copy quebra o corte `-ss`/`-t`).
- Cortes de vídeo do upscale são por CONTAGEM DE FRAMES (`-frames:v`, plano de
  `UpscaleService.PlanFrameChunks`) e a taxa vai ao ffmpeg como a FRAÇÃO exata do ffprobe
  (`"24000/1001"`). Cortar por `-t` segundos quantizava pelo fim do frame (719 e não 720
  frames a cada 30s em 23.976) e o erro por chunk acumulava contra o áudio — ~200 ms de
  desync por parte de 24 min (o bug do áudio fora de sincronia do dono, corrigido em 21/09).
  O CFR (`-fps_mode cfr`) continua obrigatório: fontes VFR têm timestamps irregulares e o
  remontar a taxa fixa desliza sem ele.
- `-progress pipe:1` em TODAS as passadas (inclusive a 1ª do SVT 2-pass): é o sinal de vida
  do watchdog de stall (`queue.stallMinutes`, default 10 min → kill + parte vira Error).

## Upscale (2 motores)

- **ONNX/DirectML** (`"onnx"`, atual): ffmpeg despeja rawvideo rgb24 na stdout → tensor NCHW
  → `InferenceSession` (AnimeJaNai 2x; 4x = 2 passes; 3x = 2x+lanczos, decisão medida) →
  stdin de outro ffmpeg que monta segmento x264. **Sessão DML é POR WORKER** — `Run`
  concorrente na mesma sessão deadlocka (cache por modelo/placa/slot).
- **ncnn/Vulkan** (legado): chunks em PNG, `realcugan`/`realesrgan`, 2 workers por GPU
  principal, progresso por contagem de PNGs.

## Encode — 3 motores (a escolha é pelo código do codec do job)

| Código | Motor | Notas |
|---|---|---|
| `svt_av1`, `svt_av1_10bit` | HandBrakeCLI (fallback: ffmpeg libsvtav1) | configs congeladas; pool CPU opcional (ParallelWorkers 1–3) |
| `nvenc_av1`, `nvenc_av1_10bit` | HandBrakeCLI (fallback: ffmpeg av1_nvenc + escada NvencBoost) | pool por placa (aba Hardware) |
| `av1an_av1`, `av1an_av1_10bit` | **Av1an** (fatia por cena, chunks paralelos com SvtAv1EncApp) | V0.39 |

Notas do Av1an (descobertas no spike de 21/09/2026, todas com evidência):

- O av1an Rust **exige VapourSynth+Python na máquina** — carrega a VSScript API no boot
  (sem VS: panic "VSScript API not available"). O app injeta no PATH do processo filho:
  tools\, a pasta do SvtAv1EncApp, o site-packages do VS (vsscript.dll) e Scripts\ do
  Python (vspipe.exe) — `AppServices.Av1anEnvPath()`.
- Sem `--trim` no av1an Rust: partes que não são o arquivo inteiro (encode direto da
  origem) passam por **pré-corte lossless** x264 qp0 + AAC 160k antes (o intermediário de
  upscale vai direto, já é um arquivo fechado).
- **`--no-defaults` sempre**: os defaults do av1an injetam `--crf 25`, que conflita com
  `--rc 1 --tbr` no modo bitrate, e `--keyint 0`, que o svt 4.x rejeita em VBR
  ("intra period must be > 0"). O app usa GOP fixo de 240 frames (10s a 24fps).
- 8/10 bits via `--pix-format yuv420p|yuv420p10le`; CQ via `--rc 0 --crf`; bitrate com
  `-p 2` (2-pass) quando Multipass+VBR.
- Progresso e watchdog pelo **logfile** (`-l`): "Queue N Workers" = total de chunks;
  "started/finished chunk" saem UMA VEZ POR CHUNK — as passadas do 2-pass rodam DENTRO do
  chunk sem linha nenhuma. `Av1anProgressTracker` (V0.40) reconstrói fps e velocidade como
  médias desde o primeiro chunk concluído (frames feitos ÷ segundo de relógio; fração de
  chunks × duração ÷ segundo de relógio), publicadas por chunk concluído e por tick de 1s
  — antes o rodapé ficava 0.0 durante chunks longos (velocidade era fixo 0). A barra de
  progresso do av1an só existe em tty.
- `ParallelWorkers` (aba Encodes, 1–3) põe N partes em paralelo; os workers internos do
  av1an = núcleos ÷ instâncias.
- **Fases no rodapé (V0.44/0.46)**: a análise de cenas (av-scenechange) é single-core por
  design (CPU parada nela é normal) e silenciosa — o log só traz o resumo final
  `scenecut: found N scene(s)`. Fases do tracker: início = "scenes" (análise em curso),
  `scenecut: found` → "preparing" (montagem dos chunks), `Segmenting video` → "segmenting",
  primeiro chunk → "chunks" (com contador done/total).
- **Análise e preparação mais rápidas (V0.46)**: `ScenecutMode` (0 precisa / 1 rápida /
  2 máxima — default 1) adiciona `--sc-downscale-height 720` (ou `360 --sc-method fast`);
  flags só deslocam ONDE os chunks cortam, video params intocados. Com o plugin
  **BestSource** do VapourSynth instalado (`pip install vapoursynth-bestsource` no mesmo
  Python do VS; o app detecta por arquivo em `AppServices.Av1anHasBestSource`), o av1an
  recebe `-m bestsource`: chunks VS frame-exatos lidos na hora — a fase "Segmenting video"
  do ffmpeg (I/O-bound, minutos com CPU parada) deixa de existir.

## Dados do usuário (`%LOCALAPPDATA%\AnimeBatch`)

- O banco mora FORA da pasta do app (V0.41): sobrevive a reinstalação/troca do pacote.
  Na 1ª execução o `data\animebatch.db` da pasta do app é copiado para lá (carry-over no
  `DbInitializer`, só quando o destino não existe) — depois o AppData é a fonte da
  verdade e o `data\` do pacote vira seed de instalação nova.
- Schema evolui por migrations EF (`Core\Data\Migrations`, `Database.Migrate()` no boot;
  `MigrationTests` roda o upgrade real de schema antigo). Gerar nova com `dotnet ef
  migrations add` (tool global).
- `chapters-edits\`: JSON por vídeo com a grade editada de capítulos (`ChapterEditsStore`,
  nome = título sanitizado + 8 hex do SHA-256 do caminho). Arquivo presente VENCE os
  capítulos do vídeo ao reabrir o episódio; "Resetar Capítulos" apaga e volta à sonda.
- Capítulos temporários (`JobItem.IsTemporary`): encodeados como qualquer parte, mas o
  merge os marca como Critical — entram no vídeo final SEM entrada de capítulo.

## Overrides por capítulo e rodapé por worker (V0.42)

- O modal de capítulo pede SÓ o tempo inicial: o fim é derivado
  (`ChapterTimeline.DeriveEnds` — início do próximo na linha do tempo; último = duração
  do vídeo) e a grade é reinserida em ordem cronológica a cada edição. JSONs antigos com
  `EndSeconds` explícito seguem carregando (o fim derivado sobrescreve na próxima edição).
- `JobItem.Preset`/`JobItem.Cq` (nullable) sobrepõem a config do codec POR PARTE; o
  `QueueRunner` monta a config efetiva (`cfg with { ... }`) antes do `EncodePartAsync`,
  então vale nos três motores (ffmpeg/HB/Av1an). No modal, o campo de bitrate vira
  Quality (CQ) quando o codec está em Qualidade Constante.
- `QueueStats` ganhou `Rate` ("500 kbps" / "CQ 22") e `Workers[]` (`WorkerStat` por slot
  ativo do `EncodeAggregator`): rodapé mostra o alvo do encode em curso e, no paralelo,
  UMA LINHA POR WORKER (parte, rate, fps, velocidade).
- Configurações: página em ScrollViewer, ferramentas trocadas por créditos (nome, papel e
  link oficial — aberto com `Launcher.LaunchUriAsync`), e seção "Base de dados" com
  arquivo/tamanho e botão "Limpar base de dados" (`ExecuteDeleteAsync` de dados de domínio;
  Settings/preferências ficam; bloqueado com a fila em execução).
- Logo: `Assets\app.ico` (exe via `ApplicationIcon` + janela via `AppWindow.SetIcon`) e
  `Assets\logo.png` no cabeçalho do menu lateral; regenerar com `scripts\make-icon.ps1`.

## Fila (`Core.Queueing.QueueRunner`)

- Um job por vez; pega o menor `Order` pendente; erro em parte/job → `Error` + mensagem
  (200 chars) e a fila **segue** pro próximo.
- **Pausar** → job `Paused` (parte volta `Pending`); **Parar** → job `Pending`.
- `RunAsync` reseta órfãos: `Error`, `Paused` e `Running` (crash/kill) voltam a processar.
- Retomada em todos os níveis: parte `Done` com arquivo presente pula o encode;
  intermediário de upscale presente pula o estágio; final presente pula o "Apenas upscaling".
- Escritas de estado de item serializadas por `_dbGate` (SemaphoreSlim) — encode paralelo
  dispara N writers e SQLite não gosta de concorrência.
- **Pools de encode** (`EncodePendingParallelAsync`): slots são `int?` — índice de placa
  NVENC (aplicado como `-gpu N`) ou `null` (CPU, comando SVT normal). NVENC resolve pelas
  GPUs da tela Hardware; SVT usa `cfg.ParallelWorkers` (1–3, aba Encodes). Cada worker pega
  a próxima parte com `Interlocked`; stats somadas pelo `EncodeAggregator`. Arquivos por
  parte não colidem (outPath e passLogBase são por `Order`).

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
