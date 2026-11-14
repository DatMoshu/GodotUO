# Merged land and cover on Linux, 2026-11-14

Windows and Android already draw the land array and merged covering land by
default (`2026-09-28_land_array_walk.md`, `2026-10-19_cover_regression.md`).
Linux and the Steam Deck kept upstream's per-chunk land. This report measures
the same default there and proposes it. The setting only changes the canvas
path: the Compatibility renderer, `--canvas-world`, and a Depth World frame
that falls back to canvas. A Depth World frame does not use it.

## Fixture

- Steam Deck LCD, SteamOS 3.8, RADV, idle (load 0.00-0.02), Desktop mode, on AC.
- One Linux Release export, the probe-instrumentation commit on top of `main`;
  the three arms differ only in client flags.
- `tools/perf_probe` scenes, 360 frames each, 1280x720 window, vsync and the
  frame cap off, classic lighting, post-processing off, live dev shard.
- Arms: as shipped; `--merged-land=array --merged-cover`; the same plus
  `--shared-cover` (`2026-10-24_shared_cover.md`).
- Each far-zoom Compatibility arm ran two or three times. Repeats agree within
  about 5%.

## Result: median frame, ms

Compatibility renderer, zoom 2.5:

| scene | as shipped | merged land and cover | plus shared cover |
|---|---:|---:|---:|
| open field | 8.7 | 6.3-6.4 | 6.3-6.4 |
| bank | 18.3-18.8 | 11.5-12.0 | 12.6 |
| forest | 11.7-12.0 | 11.0-11.4 | 10.7 |
| dungeon | 17.9-18.2 | 9.5-10.0 | 8.8-9.0 |
| docks | 16.2-17.5 | 12.5-12.8 | 11.7-11.9 |
| walk | 9.2-9.7 | 7.3-7.5 | 6.6-6.9 |

Draw calls at the bank fall from about 2,830 to 760; canvas items stay at 664
with merged cover and fall to about 50 with shared cover.

Compatibility, zoom 1: bank 6.6-6.7 to 5.3, dungeon 6.7 to 4.2, docks 7.0-7.2
to 5.2, open field 3.8-3.9 to 3.3. Canvas on Vulkan, zoom 2.5: bank 13.6-13.7
to 11.2, dungeon 11.9-12.1 to 7.6, docks 12.7-13.1 to 10.5.

## Parity

Compatibility renderer on the Deck, `--perf-quiet --perf-static-world
--perf-parity`. Ten scenes each (login, open field, bank, owner spot, forest,
dungeon, docks, Trinsic bay, cave roof, open sea):

- merged land toggled, zoom 2.5: 0 strict, 0 repeatable differing pixels;
- merged cover toggled over the land array, zoom 2.5: 0 and 0;
- merged cover toggled, zoom 1: 0 and 0.

Not checked on Linux: a moving camera, the post-processing id mirror (it turns
merged cover off and takes the old path), an Intel or NVIDIA Linux driver.

## A weak GPU: no gain at zoom 1, a small one zoomed out

A Pentium G645 with Intel HD 2000 graphics (OpenGL only, so always on canvas)
is limited by its graphics chip: whole frames are 37-64 ms while the client's
own CPU work is 1.3-8.5 ms. Merged land and cover leaves zoom 1 unchanged and
takes zoom 2.5 from 61.6 to 56.9 ms at the bank and 63.6 to 56.7 ms in the
dungeon. One dungeon reading at zoom 1 in that arm was 127 ms against 52 ms
in the other arms; a single reading, not repeated, treated as a glitch.

## Shared cover stays off

On the Deck it moves the frame by -8% to +5% on top of merged cover. On the
weak GPU it is slower at zoom 1: whole frames rise 6-10% (bank 46.4 to 49.8
ms), because its shader adds work to every sprite pixel. Item count stops
mattering once the draw calls are merged.

## Change

`Main.cs`: the Windows default block also applies on Linux.
`--merged-land=off --merged-cover=off` restore upstream's draw. This is a
default rendering change and waits for the owner's decision; it is not on
`main`.
