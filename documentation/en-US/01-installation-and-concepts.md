# 1. Installation and concepts

*[← Index](README.md) · [Next: Queue →](02-queue.md)*

---

AnimeBatch is an **open source** project for anyone who loves converting video
— **anime series** in particular, though it works for any video. The core
idea: convert each video **into separate chapters**, giving every chapter the
**bitrate that specific scene needs** — more bits for openings and fast
action, fewer for static scenes. The result is a small file whose quality is
far above that of a single-pass encode at an average bitrate (the same
approach streamers like Netflix use). The app is written in .NET (WinUI 3),
runs exclusively on Windows, and is built around the **AV1** codec for its
efficiency on anime — with three engines (SVT, NVENC and Av1an), each in 8 or
10-bit, plus AI upscaling models.

## The portable package

AnimeBatch ships as a **portable package**: there is no installer — the
downloaded folder is the whole program. Unzip it anywhere (for example
`F:\AnimeBatch\V0.61`) and run `AnimeBatchV0.61.exe` (the number follows the
version).

![Release package contents](../images/instalacao-pacote.png)

Inside the folder you'll find, among runtime files:

| Item | Purpose |
|------|---------|
| `AnimeBatchV0.61.exe` | The main executable (the version number is part of the name on purpose — don't rename the `.exe`; the `.deps` and `.runtimeconfig` files next to it must match) |
| `data\` | Holds a **seed** `animebatch.db` — the template for the real database on first run |
| `tools\` | The external tools the app uses (ffmpeg, ffprobe, mkvmerge, SVT-AV1, HandBrakeCLI, av1an etc.) — **conversion doesn't work without it** |
| `i18n\` | Available languages (`.json` files; adding a language = dropping a `.json` in there) |
| `runtimes\`, `rsrc\`, `NpuDetect\`, `Microsoft.*`/`DirectML*` DLLs | WinUI 3 runtime, DirectML and internal resources — don't touch |

> **Important:** to upgrade versions, unzip the new folder alongside and copy
> the `data\animebatch.db` from the old folder into the new one **before the
> first run** (or keep using the old database — see below). On the new
> version's first run, the seed database is copied to AppData **only if no
> database exists there yet** — the package never overwrites the AppData
> database.

## Where your data lives (survives reinstalls)

Everything you register lives **outside** the program folder, in your user
profile:

- **Database**: `%LOCALAPPDATA%\AnimeBatch\animebatch.db`
  (series, episodes, queue, history, encode settings and preferences). The
  *Settings* screen shows the current path and size.
- **Edited chapter grids**: `%LOCALAPPDATA%\AnimeBatch\chapters-edits\` — one
  `.json` per edited episode. This is what makes your chapter edits take
  precedence over the chapters embedded in the video when you reopen a file.

Deleting the program folder does **not** delete this data; to wipe everything
from inside the app, use *Settings → Database → Clear database*.

## Where converted files go

Two distinct destinations, both derived from the **conversion output folder**
(configurable in *Settings*):

- **Final file** (the complete converted episode): at the **root** of the
  output folder.
- **Intermediate parts**: in `output\AnimeBatch\{episode name}\` — one per
  chapter of the grid, named `NN - {chapter} - {file}.mkv` (e.g.
  `02 - Opening - S02E09.mkv`). These parts are what make **conversion reuse**
  possible (see [Episodes](03-episodes.md)).

## App vocabulary

- **Job** — one episode in the conversion queue. Shows up on the *Queue*
  screen with a `Pending`, `Running`, `Done` or error status.
- **Part** — a piece of the job, corresponding to **one chapter** of the grid
  (or the whole episode, if it only has the default chapter). Each part is
  encoded separately and all of them are merged at the end, with chapters.
- **Chapter** — a division of the episode defined by a **start time** (the end
  is always the next chapter's start; the last one ends at the video
  duration). The chapter grid defines how many parts the encode will have.
- **Chapter class** — *Episode*, *Opening* (OP) or *Ending* (ED/credits). The
  class determines **which series bitrate** is used for that part (see
  [Series](06-series.md)).
- **Upscaling** — increasing the resolution with a neural network (Real-CUGAN,
  Real-ESRGAN or AnimeJaNai models) on the GPU, before or alongside the encode.
- **VMAF** — a quality metric comparing the converted file against the source
  (0–100; the higher, the more faithful). The queue can compute it for each
  part when *Quality check (VMAF)* is checked.

---

*[← Index](README.md) · [Next: Queue →](02-queue.md)*
