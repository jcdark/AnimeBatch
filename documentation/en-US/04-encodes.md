# 4. Encodes

*[← Previous: Episodes](03-episodes.md) · [Index](README.md) · [Next: Hardware →](05-hardware.md)*

---

The **Encodes** tab holds the default configuration for **each codec** — what
you set here applies to every chapter that doesn't have its own override (set
in the chapter dialog on the [Episodes](03-episodes.md) screen).

![Encodes tab with the AV1 10bits codec](../images/encodes-av1.png)

Pick the **Codec** in the combo, adjust the fields and click **Save** — each
codec keeps its own configuration.

## The fields

- **Preset (1–13)** — the encoder's effort. **Lower numbers = slower and
  better quality**; higher = faster and worse. The app default is **5**. If a
  chapter doesn't set its own preset, this is the one applied to all of them.
- **Rate mode** — two mutually exclusive alternatives:
  - **Constant Quality (CQ/RF)**: you fix a quality level (e.g. 18) and the
    encoder spends whatever bits it needs. Less predictable final size. Can
    also be set per chapter (the dialog field becomes *Quality (CQ)*).
  - **Average Bitrate (kbps)**: you fix the average target — with the hint
    that it *"uses the kbps configured on each chapter in the Episodes
    screen"*. That's the app's path: the per-chapter/class series values rule.
- **Multipass encoding** (2-pass) — the first pass **analyzes** the video and
  the second encodes knowing where to spend bits. Much better results at low
  bitrate; it's the author's preferred setup (with 1 encode at a time and
  preset 5). On the footer, only pass 2 counts as converted video.
- **Fast first-pass analysis** (SVT only, average-bitrate mode) — speeds up
  the analysis pass. The author **doesn't like** using it — your call.
- **Fast conversion** (NVENC only) — zeroes the GPU encoder's lookahead for a
  speed-focused conversion.
- **Quality boost** — quality reinforcement (spatial/temporal AQ + UHQ tune +
  temporal filter): extended analysis, slower encode, avoids macroblocking.
  Doesn't combine with Fast conversion.
- **Encodes in parallel** (1, 2 or 3) — how many **parts** of the job encode
  at the same time on the CPU (SVT). With 2 or 3, the footer sums the fps of
  the running encodes. Keep in mind: each SVT process holds ~2 GB of RAM —
  on memory-tight machines the app itself limits Av1an's internal workers.
- **Scene detection** (Av1an engine) — where the chunks cut:
  *Precise (full resolution)*, *Fast (720p analysis) — recommended* or
  *Max speed (360p + fast method)*. The options only change **where** chunks
  cut; video parameters stay untouched.
- **Tune / Profile / Level**:
  - **Tune**: SVT offers *None*, *0 — VQ Delay*, *1 — Psychovisual*; NVENC
    offers *None*, *High Quality (hq)*, *Low Latency (ll)*, *Ultra Low
    Latency (ull)*, *Lossless*.
  - **Profile**: only *Main (0)* exists — the whole app is AV1 4:2:0 (the
    High and Professional profiles are 4:4:4/4:2:2 and SVT rejects them).
  - **Level**: *Auto* in practice — no need to touch it.

## Which codec should I pick? (author's recommendation)

| Goal | Codec |
|------|-------|
| Smallest file with the best quality (production) | **AV1 10bits** (SVT, 2-pass) |
| Maximum speed, larger files | **AV1 10bits NVENC** |
| SVT quality with more speed (parallel chunks) | **AV1an 10bits** |

The author's house rule: **production = AV1an/SVT p6 @ average bitrate**,
leaving NVENC for when time is what matters.

---

*[← Previous: Episodes](03-episodes.md) · [Index](README.md) · [Next: Hardware →](05-hardware.md)*
