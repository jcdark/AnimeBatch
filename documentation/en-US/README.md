# AnimeBatch — User Manual

*Leia em [Português (Brasil)](../pt-BR/README.md).*

This is the complete **AnimeBatch** user manual — the Windows desktop app for
batch anime conversion (AI upscaling + AV1 re-encode). It is organized **by
menu**, in the same order as the app's sidebar, and was written from the
author's own walkthrough video combined with the internal documentation of
every screen.

> In the manual's screenshots, series names, posters and synopsis snippets
> are covered with a mosaic for safety and copyright respect.

---

## Index

| # | Section | What you'll find |
|---|---------|------------------|
| 1 | [Installation and concepts](01-installation-and-concepts.md) | Portable package, first run, where the database and files live, app vocabulary (job, part, chapter) |
| 2 | [Queue](02-queue.md) | Start/pause/stop, reordering, editing items, VMAF, "When done" actions, how to read the status footer |
| 3 | [Episodes](03-episodes.md) | Enqueueing episodes, the chapter grid, add/edit/delete chapters, part reuse, reset |
| 4 | [Encodes](04-encodes.md) | Codecs, preset, CQ vs average bitrate, 2-pass, parallelism, scene detection, tune/profile/level |
| 5 | [Hardware (GPU)](05-hardware.md) | Workers per GPU board, automatic vs manual |
| 6 | [Series](06-series.md) | Series registry, TMDB (name or ID search), per-class bitrates, conversion history |
| 7 | [Settings](07-settings.md) | TMDB key, language, folders, upscaling GPUs, database, bundled tools and credits |

---

## Workflow overview

A typical cycle has four steps — you'll spend most of your time in
**Episodes** and **Queue**:

1. **Configure once** — In *Settings*, point the **source videos folder**
   (where your downloads appear) and the **conversion output folder**. In
   *Series*, register the series and link it to **TMDB** to set per-class
   bitrates. In *Encodes*, check the codec (the app default is SVT AV1 10-bit
   in average-bitrate mode).
2. **Enqueue** — On the *Episodes* screen, pick the folder, check the files,
   adjust the chapter grid if you want to split the episode (opening /
   episode / ending...) and click **Enqueue selected**.
3. **Convert** — On the *Queue* screen, click **Start conversion**. The
   footer shows live progress (current part, FPS, speed, ETA). Optionally,
   **Quality check (VMAF)** scores every part when it finishes, and **When
   done** can shut down/hibernate the machine for you.
4. **Collect the files** — The final file lands at the **root** of the output
   folder; intermediate parts live in
   `output\AnimeBatch\{episode name}\` and can be **reused (no re-encode)**
   if you ever convert the same episode again.

---

## Navigation

- Use the index above, or the prev/index/next arrows at the top and bottom of
  each page.
- Button and field names always appear in **bold** exactly as they read on
  screen (the app ships with an English UI under *Settings → System
  language*; screenshots show the Portuguese one).
- Input formats are shown inline, e.g. `mm:ss` for chapter start times.

---

*[Portuguese index](../pt-BR/README.md) · [First section: Installation and concepts →](01-installation-and-concepts.md)*
