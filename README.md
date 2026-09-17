# AnimeBatch

App desktop Windows para conversão de animes em lote: divisão por capítulos, encode por parte
com bitrates por série, upscale opcional (ncnn-Vulkan ou ONNX/DirectML) e união com mkvmerge —
sucessor do script `converter.py` (E:\BatchVideo).

## Recursos (por marco)

| Marco | Recursos | Status |
|---|---|---|
| **M1** | Esqueleto WinUI 3, banco SQLite local (na pasta do app), probe de episódios (ffprobe), grade de capítulos com classificação OP/ED, séries/bitrates no banco, importação do `converter.yml` legado, fila persistida com reordenação | ✅ concluído |
| **M2** | Execução da fila (Core.Queueing): SVT-AV1 (1–13, 2-pass real com turbo) e NVENC (av1_nvenc p1–p7, VBR com teto, multipass, escada de reforço de qualidade), encode paralelo por GPU | ✅ concluído |
| **M3** | Merge com mkvmerge e capítulos cumulativos OGM (Critical não vira capítulo); áudio da 1ª trilha re-encodado AAC 160k (stream-copy quebra o corte -ss/-t); legendas preservadas só no modo "Apenas upscaling" | ✅ concluído |
| **M4** | Upscale opcional: Real-CUGAN / Real-ESRGAN via ncnn-vulkan **e** AnimeJaNai via ONNX/DirectML in-process (chunks de 30s, sessão por worker, benchmark/pruning de GPU) | ✅ concluído |
| **M5** | Telas (Fila, Episódios, Encodes, Hardware, Séries+TMDB, Configurações, i18n pt-BR/en-US) + release versionado (`scripts/make-release.ps1`) | ✅ concluído |

Arquitetura e pipeline: [`docs/arquitetura.md`](docs/arquitetura.md).

## Estrutura

```
E:\AnimeBatch\
├─ src\
│  ├─ AnimeBatch.App\     — GUI WinUI 3 (.NET 9, unpackaged/self-contained)
│  ├─ AnimeBatch.Core\    — lógica sem GUI: entidades, EF Core, serviços, fila (Core.Queueing)
│  └─ AnimeBatch.Tests\   — xUnit (~150 testes: unit + integração real; pula sem tools\)
├─ docs\arquitetura.md    — pipeline, estados da fila, mapa de GPUs
├─ tools\                 — binários externos embutidos (NÃO versionados, ~500 MB)
├─ data\                  — banco animebatch.db (junto do app; portátil)
├─ scripts\               — make-release.ps1, setup-tools.ps1, smoke-latest.ps1
└─ AnimeBatch.slnx
```

## Pré-requisitos

- .NET SDK 9+ (Windows x64)
- Binários na `tools\`: `ffmpeg.exe`, `ffprobe.exe`, `mkvmerge.exe`, `mkvextract.exe`, `HandBrakeCLI.exe`,
  upscalers opcionais (`realcugan-ncnn-vulkan.exe`, `realesrgan-ncnn-vulkan.exe`) e `models-onnx\`
  (`scripts/setup-tools.ps1` baixa/copía). Pastas extras de busca: setting `tools.extraDirs`.

## Build, teste e release

```powershell
cd E:\AnimeBatch
dotnet build AnimeBatch.slnx                # NA SOLUÇÃO não use -p:Platform (o App já é x64-only)
dotnet test  AnimeBatch.slnx                # integração pula sozinha se tools\ estiver vazio
powershell -File scripts\make-release.ps1   # publish x64 → dist\AnimeBatchV<versao>\
```

Em dev, o app encontra a `tools\` da raiz do repositório subindo a árvore de diretórios a partir do exe.
O exe nasce como `AnimeBatchV<versao>.exe` (AssemblyName versionado — NÃO renomear pós-publish).
Antes de distribuir: copiar `data\animebatch.db` da versão anterior.

## Banco de dados

- SQLite via EF Core, arquivo `data\animebatch.db` **dentro da pasta do aplicativo** — mover a pasta leva os dados.
- Migrations: `dotnet ef migrations add <Nome> --project src\AnimeBatch.Core --startup-project src\AnimeBatch.Core -o Data/Migrations`
  (o upgrade real é coberto por testes — `MigrationTests`).
- Tabelas: `Series` (bitrates min/OP/ED por série + TMDB), `Keywords` (palavras OP/ED editáveis),
  `Jobs`/`JobItems` (fila com `Order`, estados por parte e retomada entre reinícios), `Setting`,
  `ConversionRecords` (histórico).
- A importação do `converter.yml` do script legado é feita uma única vez no 1º boot (duplicadas no
  YML: a primeira entrada vence, mesmo comportamento do script).

## Paridade com o script (referências)

- Nome de série normalizado: minúsculas + remoção do sufixo ` - SXXEXX…` (`ChapterService.CleanSeriesName`).
- Classificação de capítulo por substring no título: OP → Opening, ED/credits → Ending, resto → Episode
  (OP tem precedência; palavras editáveis no banco; Critical marcado à mão na tela).
- Fim de capítulo = início do próximo − 1ms; último vai até a duração total; capítulos < 0,5 s descartados.
- Nome de parte: `N - Título - Base.mkv`.
- Defaults sem série: min 500 / OP 1500 / ED 500 kbps.
