# AnimeBatch

*Leia isto em [English](README.md).*

**AnimeBatch** é um app desktop Windows para conversão de animes em lote — você aponta uma temporada, e ele divide os episódios em partes por capítulo, encodeia cada parte com o bitrate que ela merece, faz upscale com IA (opcional) e une tudo num MKV limpo com capítulos.

Construído com **WinUI 3 / .NET 9** (desempacotado, portátil x64), banco **SQLite** e ~220 testes automatizados (unitários + integração real contra as tools embarcadas).

## Por que o AnimeBatch

- **Qualidade onde importa.** Cada série tem seus próprios bitrates: um para episódios, outro para aberturas e encerramentos. Tudo mira **AV1** — NVENC por hardware ou SVT-AV1 por software — ajustado para os bitrates baixos onde o anime vive de verdade (episódio a 500 kbps é o padrão aqui).
- **Pipeline consciente de capítulos.** O episódio é dividido em partes (abertura / episódio / encerramento / capítulos manuais), cada parte é encodeada com seu próprio alvo e a união final (mkvmerge) grava capítulos OGM cumulativos. Os fins de capítulo são derivados automaticamente — você só marca os inícios.
- **Corte por frames, sem dessincronia.** O vídeo é fatiado por **contagem de frames** no fps exato da origem — inclusive taxas fracionadas como 23.976 (`24000/1001`) — para cada corte cair exatamente num frame e a imagem nunca dessincronizar do áudio, nem em episódios longos. Taxas inteiras (24/25/30 fps) são igualmente exatas; o pipeline é validado contra fontes VFR/NTSC.
- **Três motores AV1, um mesmo contrato.** NVENC AV1 (8/10 bits) com escada de reforço de qualidade (AQ espacial/temporal, tune UHQ, filtro temporal) que se recupera sozinha em drivers antigos; SVT-AV1 (8/10 bits) com 2-pass de verdade; e **Av1an** — SVT fatiado por cena com chunks paralelos, modos rápidos de detecção de cenas (downscale 720p) e chunking frame-exato com BestSource.
- **Paralelismo e múltiplas placas.** 1–3 slots de encode no CPU para os motores de software, pool NVENC que distribui as partes entre **todas as suas placas NVIDIA**, upscale que faz benchmark e ranqueia cada GPU compatível (NVIDIA, AMD ou Intel), e um rodapé de status vivo mostrando **uma linha por worker** com fps, velocidade e alvo de bitrate.
- **Upscale com IA opcional.** Real-CUGAN, Real-ESRGAN (ncnn-Vulkan) ou AnimeJaNai (ONNX/DirectML in-process, uma sessão por worker, benchmark e pruning de GPU embutidos).
- **Local e portátil.** Sem instalador, sem nuvem, sem telemetria. O banco vive em `%LOCALAPPDATA%\AnimeBatch`, as capas ficam gravadas nele, e cada release é uma pasta portátil versionada com todas as ferramentas externas embarcadas e com créditos.
- **Integração com TMDB.** Busca por nome **ou** por ID do TMDB, vínculo da série e capa baixada e guardada no banco.

## Destaques

| Área | O que você tem |
|---|---|
| Motores | NVENC AV1 / AV1 10 bits (p1–p7, VBR com teto de bitrate, `-multipass fullres`, lookahead 32, escada de reforço), SVT-AV1 / 10 bits (preset 1–13, 2-pass real, turbo na 1ª passada), Av1an (chunks por cena + workers paralelos, modos de velocidade da detecção de cenas, chunking BestSource) |
| Capítulos | Editor só com tempo inicial (fins derivados), **overrides de preset e CQ/bitrate por capítulo**, capítulo temporário, reset para a grade do vídeo |
| Upscale | Real-CUGAN / Real-ESRGAN (ncnn-Vulkan) e AnimeJaNai (ONNX/DirectML), alvos até 4K, "apenas upscaling" ou "upscaling + encode" |
| Fila | Fila persistida com reordenação/pausa/parada, remoção automática ao concluir, progresso vivo por job |
| Rodapé de status | Linhas por worker (fps · velocidade · alvo), contador de chunks, fases do pipeline (analisando cenas → preparando chunks → codificando) |
| Biblioteca | Séries com bitrates próprios, histórico de conversões, capas do TMDB, importação do YAML legado |
| Configurações | Créditos das ferramentas, tamanho da base + limpeza em um clique, pasta de saída, idioma (pt-BR / en-US) |

## Requisitos

- **Execução:** Windows 10/11 x64. Basta descompactar um release e rodar `AnimeBatchV*.exe`.
- **Motor NVENC AV1:** GPU NVIDIA classe RTX com driver **≥ 610** (NVENC API 13.1).
- **Motor Av1an (opcional):** [VapourSynth](https://www.vapoursynth.com) R80+ e Python 3.13 na máquina; o plugin BestSource (`pip install vapoursynth-bestsource`) elimina de vez a fase de segmentação.
- **Build:** .NET 9 SDK (Windows x64).

## Build e testes

```powershell
dotnet build AnimeBatch.slnx
dotnet test  AnimeBatch.slnx          # ~220 testes; integração pula sozinha sem tools\
scripts/setup-tools.ps1               # baixa as ferramentas externas para tools\ (~500 MB, não versionado)
scripts/make-release.ps1              # pacote portátil versionado em dist\
```

> Na **solução**, não passe `-p:Platform=x64` (o .slnx não declara configuração x64); a flag vale só por projeto.

## Manual do usuário

O **manual completo** está em [`documentation/`](documentation/README.md), com um guia completo por idioma — uma página por menu do app, navegação entre páginas e capturas de tela anotadas (nomes de séries cobertos por mosaico, por direitos autorais):

- 🇧🇷 **Português (Brasil)** — [`documentation/pt-BR/README.md`](documentation/pt-BR/README.md): [Instalação e conceitos](documentation/pt-BR/01-instalacao.md) · [Fila](documentation/pt-BR/02-fila.md) · [Episódios](documentation/pt-BR/03-episodios.md) · [Encodes](documentation/pt-BR/04-encodes.md) · [Hardware](documentation/pt-BR/05-hardware.md) · [Séries](documentation/pt-BR/06-series.md) · [Configurações](documentation/pt-BR/07-configuracoes.md)
- 🇺🇸 **English** — [`documentation/en-US/README.md`](documentation/en-US/README.md): [Installation and concepts](documentation/en-US/01-installation-and-concepts.md) · [Queue](documentation/en-US/02-queue.md) · [Episodes](documentation/en-US/03-episodes.md) · [Encodes](documentation/en-US/04-encodes.md) · [Hardware](documentation/en-US/05-hardware.md) · [Series](documentation/en-US/06-series.md) · [Settings](documentation/en-US/07-settings.md)

**O fluxo em resumo:** configure as pastas de origem/saída em *Configurações* → cadastre a série em *Séries* (bitrates por classe, vínculo TMDB) → em *Episódios*, marque os arquivos, divida-os em capítulos (cada capítulo recebe o bitrate que merece) e enfileire → em *Fila*, clique em *Iniciar conversão* e acompanhe o rodapé ao vivo; o *Quando terminar* pode até desligar o PC para você. Notas de arquitetura (para quem desenvolve) continuam em [`docs/arquitetura.md`](docs/arquitetura.md).

## Estrutura

```
AnimeBatch/
├─ AnimeBatch.slnx
├─ src/AnimeBatch.App/      GUI WinUI 3 (telas, fila, i18n pt-BR/en-US, assets)
├─ src/AnimeBatch.Core/     Núcleo sem GUI: modelos, EF Core + SQLite, serviços de encode, fila
├─ src/AnimeBatch.Tests/    Suíte xUnit (unitários + integração real de encode/pipeline)
├─ documentation/           Manual do usuário (guias completos em pt-BR e en-US, uma página por menu, imagens)
├─ docs/arquitetura.md      Pipeline, notas dos motores, mapa de GPUs (pt-BR)
├─ scripts/                 setup-tools, make-release, make-icon
└─ tools/                   Binários externos (baixados, NÃO versionados)
```

## Banco de dados

SQLite via EF Core. Na primeira execução, o banco semente que vem no pacote é **copiado para** `%LOCALAPPDATA%\AnimeBatch\animebatch.db` — daí em diante o AppData é a fonte da verdade e sobrevive a reinstalações. Grades de capítulos editadas ficam ao lado do banco (`chapters-edits\`). Migrations EF versionadas; o caminho de upgrade é coberto por testes.

## Créditos

O AnimeBatch se apoia nessas ferramentas, embarcadas em todo release e creditadas dentro do app: [FFmpeg](https://ffmpeg.org) (+ ffprobe), [MKVToolNix](https://mkvtoolnix.download), [HandBrake](https://handbrake.fr), [SVT-AV1](https://gitlab.com/AOMediaCodec/SVT-AV1), [Av1an](https://github.com/rust-av/av1an), [VapourSynth](https://www.vapoursynth.com), [Real-CUGAN](https://github.com/nihui/realcugan-ncnn-vulkan), [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), [AnimeJaNai](https://github.com/the-database/AnimeJaNai). Metadados do [TMDB](https://www.themoviedb.org).

---

Projeto privado — todos os direitos reservados.
