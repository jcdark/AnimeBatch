# 6. Series

*[← Previous: Hardware](05-hardware.md) · [Index](README.md) · [Next: Settings →](07-settings.md)*

---

The **Series** tab is the registry that gives the app its "memory": for each
series you set the **default bitrates per chapter class** and follow the
conversion history. Every series converted through the
[Episodes](03-episodes.md) screen shows up here automatically.

![Series detail with poster and history](../images/series-detalhe.png)

## Registry and per-class bitrates

At the top: the **series selector**, **New**, **Save**, **Delete** and
**Search TMDB**; below, the **Series name** and the three fields that define
the series' bitrate strategy:

| Field | What it controls |
|-------|------------------|
| **Episode — regular chapters (kbps)** | The common parts of the episode (dialogue, regular scenes) |
| **Opening — OP (kbps)** | The opening: music + movement — usually needs **much more** |
| **Ending — ED/credits (kbps)** | The ending/credits |

The author's practice: while the series runs 500 kbps for episodes, opening
and ending may ask for 2000 — **there's no formula**: convert, evaluate the
quality (with help from the queue's VMAF) and adjust. Once a series' standard
is set, it holds for all its episodes; only re-evaluate if the opening
changes or an action scene demands its own
[temporary chapter](03-episodes.md).

When an `.mkv` has chapters whose names indicate opening/ending, the grid is
born with these values already applied.

## Search TMDB

With the **TMDB API key** configured (see [Settings](07-settings.md)), the
**Search TMDB** button opens the link dialog:

![TMDB search dialog](../images/series-tmdb-busca.png)

- Search by **series name** or paste the **TMDB ID (optional)** directly —
  if the name is ambiguous, the ID settles it.
- Click a result and then **Link**: the app downloads the **poster** and
  **synopsis**, which start showing on the tab (stored in the database, no
  file cache).
- The registry footer shows *✓ TMDB linked: {name} (id N)*.

## Converted Files (history)

The table shows **Date · File · Duration · Size** for each converted episode
of the series. **Repeated names in the list are normal**: each row is one
finished conversion — re-encoding a part whose bitrate came out too low adds
a new row. That's how you track the final size you're achieving per episode.

---

*[← Previous: Hardware](05-hardware.md) · [Index](README.md) · [Next: Settings →](07-settings.md)*
