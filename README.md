# AnimeBatch

*Read this in [Português (Brasil)](README_PTBR.md).*

**AnimeBatch** is a Windows desktop app for batch anime conversion — feed it a season, and it splits episodes into chapter-aware parts, encodes each part with the bitrate it deserves, optionally upscales with AI, and merges everything into a clean MKV with proper chapters.

Built with **WinUI 3 / .NET 9** (unpackaged, portable x64), backed by **SQLite**, with ~220 automated tests (unit + real integration runs against the bundled tools).

## Why AnimeBatch

- **Quality where it matters.** Each series carries its own bitrates: one for regular episodes, another for openings and endings. Everything targets **AV1** — hardware NVENC or software SVT-AV1 — tuned for the low bitrates where anime actually lives (500 kbps episodes are the norm here).
- **Chapter-aware pipeline.** Episodes are split into parts (opening / episode / ending / custom chapters), each part is encoded with its own target, and the final merge (mkvmerge) writes cumulative OGM chapters. Chapter ends are derived automatically — you only pick start times.
- **Frame-exact, sync-safe cutting.** The video is sliced by **frame counts** at the source's exact frame rate — including fractional rates such as 23.976 (`24000/1001`) — so every chunk boundary lands exactly on a frame and the picture never drifts against the audio, no matter how long the episode is. Integer rates (24/25/30 fps) are just as exact; the pipeline is validated against VFR/NTSC sources.
- **Three AV1 engines, one contract.** NVENC AV1 (8/10-bit) with a quality-boost ladder (spatial/temporal AQ, UHQ tune, temporal filter) that self-heals on older drivers; SVT-AV1 (8/10-bit) with real 2-pass; and **Av1an** — scene-chunked SVT with parallel chunk workers, fast scene-detection modes (720p downscale) and BestSource frame-accurate chunking.
- **Parallelism and multi-GPU.** 1–3 CPU encode slots for the software engines, an NVENC pool that spreads parts across **all your NVIDIA boards**, upscaling that benchmarks and ranks every compatible GPU (NVIDIA, AMD or Intel), and a live status footer showing **one line per worker** with its fps, speed and target bitrate.
- **Optional AI upscaling.** Real-CUGAN, Real-ESRGAN (ncnn-Vulkan) or AnimeJaNai (ONNX/DirectML, in-process, one session per worker, GPU benchmark and pruning built in).
- **Local and portable.** No installer, no cloud, no telemetry. The database lives in `%LOCALAPPDATA%\AnimeBatch`, covers are stored in it, and every release is a versioned portable folder with all external tools bundled and credited.
- **TMDB integration.** Search by name *or* by TMDB ID, link the series, and the cover is downloaded and kept in the database.

## Feature highlights

| Area | What you get |
|---|---|
| Engines | NVENC AV1 / AV1 10-bit (p1–p7, VBR with bitrate ceiling, `-multipass fullres`, lookahead 32, quality-boost ladder), SVT-AV1 / 10-bit (preset 1–13, real 2-pass, turbo first pass), Av1an (scene chunks + parallel chunk workers, scene-detection speed modes, BestSource chunking) |
| Chapters | Start-time-only editor with derived ends, per-chapter **preset and CQ/bitrate overrides**, temporary chapters, reset-to-source grid |
| Upscale | Real-CUGAN / Real-ESRGAN (ncnn-Vulkan) and AnimeJaNai (ONNX/DirectML), targets up to 4K, "upscale only" or "upscale + encode" |
| Queue | Persistent queue with reorder/pause/stop, auto-remove on completion, per-job live progress |
| Status footer | Per-worker lines (fps · speed · target rate), chunk counter, pipeline phases (analyzing scenes → preparing chunks → encoding) |
| Library | Series with per-series bitrates, conversion history, TMDB covers, legacy YAML import |
| Settings | Tool credits, database size + one-click wipe, output directory, UI language (pt-BR / en-US) |

## Requirements

- **Runtime:** Windows 10/11 x64. Unzip a release and run `AnimeBatchV*.exe` — that's it.
- **NVENC AV1 engine:** NVIDIA RTX-class GPU with driver **≥ 610** (NVENC API 13.1).
- **Av1an engine (optional):** [VapourSynth](https://www.vapoursynth.com) R80+ and Python 3.13 on the machine; the BestSource plugin (`pip install vapoursynth-bestsource`) removes the segmenting pass entirely.
- **Build:** .NET 9 SDK (Windows x64).

## Build & test

```powershell
dotnet build AnimeBatch.slnx
dotnet test  AnimeBatch.slnx          # ~220 tests; integration tests skip gracefully without tools\
scripts/setup-tools.ps1               # downloads external tools into tools\ (~500 MB, not committed)
scripts/make-release.ps1              # versioned portable package into dist\
```

> Build the **solution** without `-p:Platform=x64` (the .slnx has no x64 solution configuration); the flag is project-level only.

## Project layout

```
AnimeBatch/
├─ AnimeBatch.slnx
├─ src/AnimeBatch.App/      WinUI 3 GUI (pages, queue UI, i18n pt-BR/en-US, assets)
├─ src/AnimeBatch.Core/     Engine-free core: models, EF Core + SQLite, encode services, queue runner
├─ src/AnimeBatch.Tests/    xUnit suite (unit + real encode/pipeline integration)
├─ docs/arquitetura.md      Pipeline, engine notes, GPU map (pt-BR)
├─ scripts/                 setup-tools, make-release, make-icon
└─ tools/                   External binaries (downloaded, NOT committed)
```

## Database

SQLite via EF Core. On first run the seed database shipped in the package is **copied to** `%LOCALAPPDATA%\AnimeBatch\animebatch.db` — from then on AppData is the source of truth and survives reinstalls. Edited chapter grids are saved alongside it (`chapters-edits\`). Versioned EF migrations; the upgrade path is covered by tests.

## Credits

AnimeBatch stands on these tools, bundled with every release and credited in-app: [FFmpeg](https://ffmpeg.org) (+ ffprobe), [MKVToolNix](https://mkvtoolnix.download), [HandBrake](https://handbrake.fr), [SVT-AV1](https://gitlab.com/AOMediaCodec/SVT-AV1), [Av1an](https://github.com/rust-av/av1an), [VapourSynth](https://www.vapoursynth.com), [Real-CUGAN](https://github.com/nihui/realcugan-ncnn-vulkan), [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), [AnimeJaNai](https://github.com/the-database/AnimeJaNai). Metadata from [TMDB](https://www.themoviedb.org).

---

Private project — all rights reserved.
