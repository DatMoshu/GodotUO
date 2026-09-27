# tools/bg_videos: the built-in background loops

This tool renders the ten looping videos that the canvas background
(ADR-0016) offers as `builtin:<name>`. Everything is procedural (numpy, scipy
and PIL, encoded by ffmpeg's libtheora), so the output is original art and
CC0. See `godot/GUO/assets/backgrounds/LICENSE.md`.

```
python tools\bg_videos\run.py                        all ten, 1280x720, into godot\GUO\assets\backgrounds
python tools\bg_videos\run.py --only twin-moons      one theme
python tools\bg_videos\run.py --size 1920x1080 --out D:\bg_masters   masters, kept outside the repo
python tools\bg_videos\run.py --set screensavers   the OLED screensavers, into godot\GUO\assets\screensavers
```

## What it writes

Per theme it writes:

- `<name>.ogv`: Ogg Theora, 24 fps, yuv420p, with no audio track.
- `<name>.png`: the exact first frame, at the same size. This is the still
  that low-power mode shows.

It also writes `backgrounds.json`, which the Options dropdown reads:
`{"name", "title", "video", "still"}`. The paths are relative to the folder.

A contact sheet (four phases per theme) and `report.json` go to
`build/bg_videos/`.

## Spec it holds to

These rules were agreed with the background player:

- **Size and rate:** 1280x720 at 24 fps. The node cover-scales it. Theora
  decodes on the CPU on phones, and 720p keeps that cheap.
- **Loop length:** 20 to 30 s, with no fade at the seam. Every theme is a
  pure function of the loop phase p in [0, 1):
  - noise volumes are FFT-generated and periodic in time;
  - particles travel a whole number of screen wraps per loop;
  - every flicker and sway is a whole number of sine cycles.
- **Seam check:** each run renders p = 1.0 and compares it with frame 0. The
  report shows `seam` as the maximum difference in 8-bit levels: 0, or 1 from
  rounding.
- **Look:** calm, dark-leaning and low-contrast, with no text and no bright
  hot spots, because the UI always draws over it.
- **Frame 0 matters:** it is also the still used in low-power mode.
- **Budget:** at most 6 MB per `.ogv` and 50 MB for all of them, because they
  ship in the APK. At `--q 8`, all ten come to about 17 MB.
- **Dither:** a static dither is added before quantising. It stops dark
  gradients from banding and costs almost nothing in inter frames.

## Themes

| name | seconds | what it is |
|---|---:|---|
| moongate-shimmer | 24 | a swirling blue oval gate with orbiting motes |
| candle-parchment | 24 | dark parchment lit by a flickering candle, with drifting dust |
| starlit-sea | 24 | twinkling stars and a glittering sea path under an off-screen moon |
| drifting-fog | 30 | fog layers over dim hill ridges |
| ember-drift | 24 | embers rising from a low glow, with faint smoke |
| rain-on-stone | 24 | rain streaks and ripples on wet cobbles |
| aurora-night | 30 | aurora curtains over mountain silhouettes |
| snowfall-pines | 30 | three layers of pines and parallax snowfall |
| sunken-caustics | 24 | caustic light on a seabed, light shafts and bubbles |
| twin-moons | 30 | a silver and a red moon behind drifting clouds |

To add a theme, subclass `Theme`, add it to `THEMES`, and keep everything a
function of `p`.

## Screensavers (`--set screensavers`)

`screensavers.py` holds four loops for OLED screens. They follow the rules
above, with these differences:

- **Output:** they go to `godot/GUO/assets/screensavers/`, listed in
  `screensavers.json`. It has the same fields as `backgrounds.json` (see
  data_formats.md, "Screensavers in the client").
- **Store-only loops:** a theme with `store_only = True` gets
  `"store_only": true`. The client does not offer it; instead
  `tools/asset_store/seed.py` publishes it as a `screensaver` pack.
- **Loop length:** 32 to 40 s.
- **Black and moving:** the screen is mostly true black, and nothing stays
  lit in one place. A theme either drifts its whole frame on a slow
  Lissajous path, or keeps every lit element moving; a fixed glow breathes
  once per loop instead of holding steady.
- **Burn-in check:** the report measures this. `static_max` is the
  brightest level (0 to 255) any pixel keeps through the whole loop, and
  `mean_level` is the average level. All four keep `static_max` at 2 or
  lower.

The report and contact sheet go to `build/bg_videos/screensavers/`.

| name | seconds | what it is |
|---|---:|---|
| moongate-embers | 36 | a small blue gate wandering the dark, shedding embers from its rim |
| drifting-reagents | 32 | rose petals, pearls and ash tumbling slowly down (store only) |
| spine-starfield | 40 | a starfield and the odd meteor over a black mountain range with a moonlit crest |
| sigil-trace | 36 | a gold pen tracing an original seven-pointed rune, which then fades to embers |
