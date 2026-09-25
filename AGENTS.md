# AnimeBatch — instruções do workspace

App Windows (WinUI 3, .NET) de conversão/upscaling/remux de anime em lote. Substituiu o antigo `converter.py`.

## Layout

- `AnimeBatch.slnx` — solução única.
- `src/AnimeBatch.App` — app WinUI 3 (UI + fila + QueueRunner).
- `src/AnimeBatch.Core` — núcleo (jobs, encodes, ONNX/DirectML, SQLite).
- `src/AnimeBatch.Tests` — xUnit (~200 testes).
- Banco SQLite em `%LOCALAPPDATA%\AnimeBatch\animebatch.db` (V0.41; sobrevive a reinstalação). Na 1ª execução o `data\animebatch.db` da pasta do app (que segue no pacote como seed) é COPIADO para lá — nunca o contrário; depois disso o AppData é a fonte da verdade. Migrations EF versionadas em `src/AnimeBatch.Core/Data/Migrations` (gerar com `dotnet ef migrations add`, tool global). `chapters-edits\` ao lado do banco guarda as grades de capítulos editados por vídeo.
- `scripts/` — release (`make-release.ps1`) e utilitários.
- `docs/`, `tools/` — documentação e auxiliares.

## Build / teste

```
dotnet build AnimeBatch.slnx
dotnet test  AnimeBatch.slnx
```

- Na SOLUÇÃO, NÃO passe `-p:Platform=x64` — o slnx não declara config de solução x64 e o build morre com MSB4126. O App só tem a plataforma x64 (sempre compila x64) e Core/Tests são AnyCPU rodando em processo x64.
- `-p:Platform=x64` é válido no nível de PROJETO (é o que `scripts/make-release.ps1` usa no publish do App).
- Testes de integração pulam sozinhos se `tools\` não estiver populado (script `setup-tools.ps1`).
- Release: `scripts/make-release.ps1` e depois copiar `data\animebatch.db` da versão anterior.
- NÃO renomear o exe pós-publish (sidecars .deps/.runtimeconfig casam com o nome; usar `AssemblyName=AnimeBatchV$(Version)`).

## Regras do dono (firmes)

- **Encoders congelados** (decisão 17/09/2026): NVENC p7 @500k segue pior que SVT p6 @500k em bitrate baixo e o dono decidiu NÃO mexer — não oferecer híbrido por classe nem logging de comandos.
- Configs antigas sem nova chave devem continuar funcionando (System.Text.Json preserva initializer; defaults ligam recursos novos).
- Sources estão em `G:\Dublados`; finais vão para a raiz do drive de saída (F:\), partes em `F:\AnimeBatch\{base}`.

## Gotchas rápidos

- Reatribuir a MESMA `List<T>` ao ItemsSource é no-op no WinUI → usar `ObservableCollection`.
- `CheckBox` não tem `CheckedChanged` (isso é WPF) → eventos `Checked`/`Unchecked`.
- `x:Bind` default é `OneTime`; label dinâmico pede `Mode=OneWay`.
- Fontes VFR/NTSC (23.976): cortes de vídeo do upscale são por CONTAGEM DE FRAMES (`-frames:v`, `UpscaleService.PlanFrameChunks`) com a FRAÇÃO do ffprobe (`-r 24000/1001`) — cortar por `-t` segundos quantizava pelo fim do frame e o erro por chunk acumulava ~4,2 ms → ~200 ms de desync por parte de 24 min (bug do áudio fora de sincronia, corrigido 21/09). O `-fps_mode cfr` segue obrigatório (senão o áudio desliza em VFR).
- Encodes em paralelo no CPU: `CodecEncodeConfig.ParallelWorkers` (1–3, aba Encodes, só SVT) → pool de slots `int?` no `EncodePendingParallelAsync`; `passLogBase`/`outPath` são por `Order`, então não colidem. NVENC segue com pool por placa (aba Hardware).
- Motor Av1an (códigos `av1an_*`, V0.39): exige av1an.exe + SvtAv1EncApp.exe em `tools\` E VapourSynth+Python na máquina (VSScript API; PATH extra montado em `AppServices.Av1anEnvPath()`). Usar `--no-defaults` SEMPRE (defaults do av1an: `--crf 25` conflita com VBR e `--keyint 0` quebra o svt 4.x em VBR) — GOP 240. Sem `--trim`: parte cortada = pré-corte lossless antes. Progresso pelo logfile `-l`, não pelo stderr — `started/finished chunk` saem 1x POR CHUNK (2-pass roda dentro do chunk, sem linha); fps/speed são médias do `Av1anProgressTracker` (V0.40: rodapé 0.0 no paralelo era o relato antigo, só-por-chunk e speed fixo 0). V0.43: o pré-corte relata progresso (era `null`) e o tail publica HEARTBEAT de 1s mesmo sem chunk concluído — scenecut+segmentação (~4 min no KAIJU S01E09) congelavam o rodapé com FPS 0.0/Elapsed 00:00 e parecia que não convertia; tick injetável via `av1anTailTickMs` p/ testes. V0.44: o tracker expõe fase e chunks done/total — rodapé mostra "chunk 3/56"/fases. No av1an NÃO existe "passo 1/2": as 2 passadas rodam DENTRO de cada chunk (em paralelo), sem linha no log entre elas.
- Av1an acelerado (V0.46): a análise de cenas (av-scenechange) é SINGLE-CORE por design — CPU parada (~1 núcleo) nela é normal; o log não emite linha NENHUMA até o resumo "scenecut: found N" (que encerra a análise). Fases do tracker: inicial = "scenes" (análise silenciosa), "scenecut: found" → "preparing" (montagem dos chunks, era mostrada como análise), "Segmenting video" → "segmenting", chunks → "chunks". `CodecEncodeConfig.ScenecutMode` (0 precisa / 1 rápida 720p — DEFAULT / 2 máxima 360p+fast) vira `--sc-downscale-height [720|360]` + `--sc-method fast`; as flags só mudam ONDE os chunks cortam, video params intocados. Com o plugin BestSource do VapourSynth instalado (pip `vapoursynth-bestsource` no MESMO Python do VS; probe por ARQUIVO `site-packages\vapoursynth\plugins\libbestsource.dll` em `AppServices.Av1anHasBestSource` — hook Mimosa BLOQUEIA novo `Process.Start`/`ArgumentList.Add`, nem tente), o av1an ganha `-m bestsource`: chunks VS frame-exatos sem a fase "Segmenting video" do ffmpeg (I/O-bound, minutos). Avisar o dono: análise segue single-core, o que cai é o TEMPO.
- Teste de integração NVENC pula (não falha) com driver velho: `IntegrationHelpers.SkipIfNoNvenc` faz probe de 2 frames com av1_nvenc e trata "minimum required Nvidia driver" (ffmpeg de tools\ exige driver ≥ 610 / API 13.1; máquina ficou em 591.86) — atualizar o driver NVIDIA resolve de verdade.
- Grade de capítulos (V0.41): adicionar/editar/remover capítulo na tela Episódios grava a grade inteira em `%LOCALAPPDATA%\AnimeBatch\chapters-edits\{nome}-{hash8}.json` (`ChapterEditsStore`) e, com arquivo salvo, ela VENCE os capítulos do vídeo ao reabrir; "Resetar Capítulos" apaga o arquivo e volta à sonda + bitrates da série.
- Modal de capítulo pede SÓ o tempo inicial (V0.42): o fim é DERIVADO (`ChapterTimeline.DeriveEnds`) — início do próximo capítulo na linha do tempo, último = duração do vídeo; adicionar/editar reinsere em ordem cronológica e recalcula os fins de toda a grade. JSONs antigos com EndSeconds continuam carregando.
- Overrides por capítulo (V0.42): `JobItem.Preset`/`JobItem.Cq` (nullable; null = config do codec). O `QueueRunner` compõe `EffectiveCfg` (`cfg with { Preset/Cq }`) antes do `EncodePartAsync` — vale para TODOS os motores (ffmpeg/HB/Av1an, que clampeiam a faixa). No modal: preset é combo "Padrão" + 1..N; o campo vira Quality (CQ) quando o codec está em Qualidade Constante, senão kbps.
- Rodapé (V0.42): `QueueStats` carrega `Rate` ("500 kbps" ou "CQ 22") no sequencial e `Workers[]` (um `WorkerStat` por slot ativo do `EncodeAggregator`) no paralelo — a MainWindow imprime uma linha por worker.
- Rodapé multipass (V0.50): em encode 2-pass SÓ a última passada conta como "Video converted"/percentual (passadas anteriores escrevem em NUL — antes o rodapé somava o vídeo 2x). No HB, fps usa o "avg fps" da própria linha (o instantâneo pula 20→90 com a cena) e speed é a MÉDIA desde o começo da passada (`smoothPassDoneSeconds/elapsed`; o delta entre linhas era ruído puro). No ffmpeg 2-pass, a passada 1 reporta `OutTimeSeconds=0` via `WithPass(..., zeroOutTime: true)`.
- Capítulo temporário (`JobItem.IsTemporary`, V0.41): encodeia como parte normal mas o merge o recebe como Critical — conteúdo entra no final SEM entrada de capítulo (usar para dividir o encode com bitrate próprio sem poluir a lista de capítulos).
- Capa de série é base64 NO BANCO (`Series.CoverImageBase64`, V0.41) — o cache de arquivo em data\posters foi aposentado; TMDB baixa → `SeriesRepository.UpdateCoverAsync` → BitmapImage de MemoryStream (`AsRandomAccessStream`).
- Logo/ícone (V0.42): `Assets\app.ico` é o ícone do exe (`ApplicationIcon` no csproj) E da janela (`AppWindow.SetIcon` no boot — app desempacotado não lê do manifesto); `Assets\logo.png` vai no cabeçalho do NavigationView. Regenerar o .ico com `scripts\make-icon.ps1` (System.Drawing; 100% ASCII).
- Configurações (V0.42): página inteira em ScrollViewer; ferramentas viraram CRÉDITOS (nome + papel + link via `Launcher.LaunchUriAsync` — NÃO usar `Process.Start` p/ URL); seção "Base de dados" mostra arquivo/tamanho e "Limpar base de dados" faz `ExecuteDeleteAsync` de JobItems→Jobs→Conversions→Series→Keywords (mantém Settings/preferências), bloqueado com a fila rodando.
- Tela Episódios memoriza a última pasta (setting `episodes.lastFolder`), que vence a Pasta Origem Vídeos no boot da aba.
- Sessão ONNX/DirectML é POR WORKER (`Run` concorrente na mesma sessão deadlocka).

Detalhes completos (histórico de versões, receitas FFmpeg/NVENC, mapa de GPUs) estão na memória do projeto (workspace `E:\AnimeBatch`).
