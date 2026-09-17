# AnimeBatch

App desktop Windows para conversão de animes em lote: divisão por capítulos, encode por parte
com bitrates por série, e união com mkvmerge — sucessor do script `converter.py` (E:\BatchVideo).

## Recursos (por marco)

| Marco | Recursos | Status |
|---|---|---|
| **M1** | Esqueleto WinUI 3, banco SQLite local (na pasta do app), probe de episódios (ffprobe), grade de capítulos com classificação OP/ED, séries/bitrates no banco, importação do `converter.yml` legado, fila persistida com reordenação | ✅ concluído |
| **M2** | Execução da fila: HandBrake (SVT-AV1 10-bit, multi-pass estrito) e ffmpeg NVENC (av1_nvenc 10-bit: vbr + maxrate + bufsize + rc-lookahead + multipass) | 🔜 |
| **M3** | Remux preservando TODAS as trilhas de áudio/legendas + merge com capítulos cumulativos | 🔜 |
| **M4** | Upscale/restauração opcional (Real-CUGAN / realesr-animevideov3 via ncnn-vulkan) | 🔜 |
| **M5** | Polimento das telas + empacotamento (zip self-contained) | 🔜 |

## Estrutura

```
E:\AnimeBatch\
├─ src\
│  ├─ AnimeBatch.App\     — GUI WinUI 3 (.NET 9, unpackaged/self-contained)
│  ├─ AnimeBatch.Core\    — lógica sem GUI: entidades, EF Core, serviços
│  └─ AnimeBatch.Tests\   — xunit (Core)
├─ tools\                 — binários externos embutidos (NÃO versionados)
├─ data\                  — banco animebatch.db (junto do app; portátil)
├─ scripts\setup-tools.ps1 — copia/baixa os binários pra tools\
└─ AnimeBatch.slnx
```

## Pré-requisitos

- .NET SDK 9+ (Windows x64)
- Binários na `tools\`: `ffmpeg.exe`, `ffprobe.exe`, `mkvmerge.exe`, `mkvextract.exe`, `HandBrakeCLI.exe`
  (opcional no futuro: `realcugan-ncnn-vulkan.exe`, `realesrgan-ncnn-vulkan.exe` + modelos)

## Build e execução (dev)

```powershell
cd E:\AnimeBatch
powershell -File scripts\setup-tools.ps1        # popula tools\ (só na 1ª vez)
dotnet test src\AnimeBatch.Tests                # testes do Core
dotnet build -p:Platform=x64 src\AnimeBatch.App
.\src\AnimeBatch.App\bin\x64\Debug\net9.0-windows10.0.19041.0\AnimeBatch.App.exe
```

Em dev, o app encontra a `tools\` da raiz do repositório subindo a árvore de diretórios a partir do exe.

## Banco de dados

- SQLite via EF Core, arquivo `data\animebatch.db` **dentro da pasta do aplicativo** — mover a pasta leva os dados.
- Migrations: `dotnet ef migrations add <Nome> --project src\AnimeBatch.Core --startup-project src\AnimeBatch.Core -o Data/Migrations`
- Tabelas: `Series` (bitrates min/OP/ED por série), `Keyword` (palavras OP/ED editáveis), `Job`/`JobItem`
  (fila com `Order` para reordenação e retomada entre reinícios), `Setting`.
- A importação do `converter.yml` do script legado é feita uma única vez na aba **Séries** (duplicadas no
  YML: a primeira entrada vence, mesmo comportamento do script).

## Paridade com o script (referências)

- Nome de série normalizado: minúsculas + remoção do sufixo ` - SXXEXX…` (`ChapterService.CleanSeriesName`).
- Classificação de capítulo por substring no título: OP → `Max`, ED/credits → `End`, resto → `Min`
  (OP tem precedência; palavras agora editáveis no banco).
- Fim de capítulo = início do próximo − 1ms; último vai até a duração total; capítulos < 0,5 s descartados.
- Nome de parte: `N - Título - Base.mkv`.
- Defaults sem série: min 500 / OP 1500 / ED 500 kbps.
