# Canvas Background

What fills the window behind the game when the world view does not cover it
(a phone in landscape with the world letterboxed, a desktop window larger
than the game area). Source of truth: ADR-0016 (Accepted) and
`tools\bg_videos\README.md`.

Upstream paints flat grey there. GUO draws a `CanvasLayer` at layer -1
(`src\Render\CanvasBackground.cs`) under everything, cover-scaled to the
window. In the default mode the node is inactive and nothing changes.

## Modes

| Mode | Draws |
|---|---|
| `builtin-grey` | Upstream's flat grey. The default; the node does nothing. |
| `builtin-wood` | A wood tile GUO owns, generated at first use (256x256, procedural). |
| `image` | A picture of yours, from `CanvasBackgroundPath`. |
| `video` | A looping, muted `.ogv` (Ogg Theora) of yours. |
| `frames` | A folder of numbered PNGs, or one sprite-sheet strip of square frames, played at `CanvasBackgroundFps` (default 12). |
| `builtin:<name>` | One of the ten shipped loops below. |

A path that does not exist falls back to `builtin-grey`. The background is
sampled `Nearest` for the wood tile (it is pixel art) and `Linear` for a
player's picture, video or frames; nothing else in the client changes its
filter.

## The ten built-in loops

Original procedural art, CC0, rendered by `python tools\bg_videos\run.py`
into `godot\GUO\assets\backgrounds\` as `<name>.ogv` (1280x720, 24 fps,
Theora, no audio) plus `<name>.png`, the exact first frame used as the
low-power still. `backgrounds.json` there is the manifest the client reads.
Every theme is a pure function of the loop phase, so the last frame equals
the first and the loop has no seam.

`moongate-shimmer`, `candle-parchment`, `starlit-sea`, `drifting-fog`,
`ember-drift`, `rain-on-stone`, `aurora-night`, `snowfall-pines`,
`sunken-caustics`, `twin-moons`.

To re-render one, `run.py --only ember-drift`; to render masters at another
size, `run.py --size 1920x1080 --out <folder outside the repo>`. A contact
sheet and a seam report land in `build\bg_videos\`.

## Choosing one

In the game, Options, the Display page, the Background section: pick a mode,
browse for a file, set the frame rate for `frames`, tick low power. (ADR-0016
places the section under Display; the commit that built it says Video. Look
on both pages.) The choice is saved in the character's profile:

```
CanvasBackgroundMode   "builtin-grey"
CanvasBackgroundPath   ""
CanvasBackgroundFps    12
CanvasBackgroundLowPower  false
```

For a scripted run, `--background <mode>[:<path>][@fps][,lowpower]` overrides
the profile and is never saved, for example `--background builtin:starlit-sea`
or `--background frames:D:\loops\rain@8,lowpower`.

## Adding your own

- **A picture**: any image Godot loads; mode `image`.
- **A video**: encode to Ogg Theora (`.ogv`), the one video format Godot
  plays without a plugin; mode `video`. It loops and is muted.
- **Frames**: a folder of PNGs in natural order (`frame_2` before
  `frame_10`), or one PNG whose name ends in a strip suffix and holds square
  frames side by side; mode `frames`. On a phone a picked file is copied into
  the user directory, because a content URI cannot be reopened later; a
  folder cannot be, so on a phone `frames` means a sheet.
- **A new built-in**: add a theme to `tools\bg_videos`, render it, and it
  appears in `backgrounds.json`; the client lists whatever the manifest
  lists.

## Low power

With `CanvasBackgroundLowPower` on, a video pauses once its first frame has
decoded and `frames` never advances: the background becomes a still. Mobile
and Web profiles get it on through the per-platform table at profile version
6 (see [Mobile UI](Mobile-UI.md)); the desktop leaves it off.

## What was verified

ADR-0016's validation section covers the mode switch, the profile round
trip, the fallback on a missing path and the migration to version 6. The
loops' seamlessness is the tool's seam report, checked when they were
rendered.
