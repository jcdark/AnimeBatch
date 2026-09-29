# AnimeBatch — User Manual

*Leia em [Português (Brasil)](README.md).*

This is the complete **AnimeBatch** user manual — the Windows desktop app for
batch anime conversion (AI upscaling + AV1 re-encode). It is organized **by
menu**, in the same order as the app's sidebar, and was written from the
author's own walkthrough video combined with the internal documentation of
every screen.

> **Note on language:** the section pages below are written in **Portuguese
> (pt-BR)**, matching the app's default UI language. The app itself ships
> with an English UI too (*Settings → System language*), and the sections map
> 1:1 to the menus, so the guide is usable in English as well.
>
> In the manual's screenshots, series names, posters and synopsis snippets
> are covered with a mosaic for safety and copyright respect.

---

## Index

| # | Section (page in pt-BR) | What you'll find |
|---|-------------------------|------------------|
| 1 | [Instalação e conceitos](01-instalacao.md) | Portable package, first run, where the database and files live, app vocabulary (job, part, chapter) |
| 2 | [Fila](02-fila.md) | Start/pause/stop, reordering, editing items, VMAF, "When done" actions, how to read the status footer |
| 3 | [Episódios](03-episodios.md) | Enqueueing episodes, the chapter grid, add/edit/delete chapters, part reuse, reset |
| 4 | [Encodes](04-encodes.md) | Codecs, preset, CQ vs average bitrate, 2-pass, parallelism, scene detection, tune/profile/level |
| 5 | [Hardware (GPU)](05-hardware.md) | Workers per GPU board, automatic vs manual |
| 6 | [Séries](06-series.md) | Series registry, TMDB (name or ID search), per-class bitrates, conversion history |
| 7 | [Configurações](07-configuracoes.md) | TMDB key, language, folders, upscaling GPUs, database, bundled tools and credits |

---

## Workflow overview

A typical cycle has four steps — you'll spend most of your time in
**Episódios** (Episodes) and **Fila** (Queue):

1. **Configure once** — In *Configurações* (Settings), point the **source
   folder** (where your downloads appear) and the **conversion output
   folder**. In *Séries* (Series), register the series and link it to
   **TMDB** to set per-class bitrates. In *Encodes*, check the codec (the app
   default is SVT AV1 10-bit in average-bitrate mode).
2. **Enqueue** — On the *Episódios* screen, pick the folder, check the files,
   adjust the chapter grid if you want to split the episode (opening /
   episode / ending...) and click **Enfileirar selecionados** (Enqueue
   selected).
3. **Convert** — On the *Fila* (Queue) screen, click **Iniciar conversão**.
   The footer shows live progress (current part, FPS, speed, ETA). Optionally,
   **Verificar qualidade (VMAF)** scores every part when it finishes, and
   **Quando terminar** (When done) can shut down/hibernate the machine for
   you.
4. **Collect the files** — The final file lands at the **root** of the output
   folder; intermediate parts live in
   `output\AnimeBatch\{episode name}\` and can be **reused (no re-encode)**
   if you ever convert the same episode again.

---

## Navigation

- Use the index above, or the prev/index/next arrows at the top and bottom of
  each page.
- Button and field names always appear in **bold** exactly as they read on
  screen (in Portuguese).
- Input formats are shown inline, e.g. `mm:ss` for chapter start times.

---

*[Portuguese index](README.md) · [First section: Installation & concepts (pt-BR) →](01-instalacao.md)*
