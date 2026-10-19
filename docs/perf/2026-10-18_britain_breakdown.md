# Where the world frame goes (PERF-03): the "before" table

2026-10-18, 23:40-00:00 UTC. RTX 4090 (driver 32.0.16.1714, Forward+), Ryzen 7
7800X3D, Windows 11, GUO main 96ce0ff2 plus the stage timers of this branch,
optimised build, vsync, the frame cap and upstream's draw pacing off, the
Classic look (no post-processing), 360 frames a scene. A quiet window on the
GUO side (no other GUO client, build or Godot run); CPU load from other
projects on the machine read 22-48% between runs. The dev shard as populated
today. Stage times come from `src/Render/PerfStages.cs`, read by the perf
probe per frame; they are taken at stage boundaries and when the kind of
object drawn changes, not per sprite.

Scenes: the five of the 2026-09-27 baseline, the run, and two new ones for
the community's cases: `cave-roof`, a crypt at z -60 under land at z 0 near
Yew (1010, 760), and `open-sea`, between Vesper and Moonglow (3600, 1400).
The community's dense city is `britain-bank`, their forest `dense-forest`.

## Zoomed out: 2560x1440, zoom 2.5

| Scene | mean ms | FPS | render frame ms | outside the client's counters ms | gather ms | mesh build ms | gather tail ms | land mesh ms | loose land ms | sort ms | statics ms | covering land ms | mobiles ms | items ms | multis ms | other ms | transparent ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| open-field | 8.1 | 123.1 | 5.6 | 2.5 | 2.61 | 0.00 | 0.01 | 0.35 | 0.00 | 0.32 | 1.24 | 0.53 | 0.09 | 0.01 | 0.00 | 0.00 | 0.00 |
| britain-bank | 39.8 | 25.2 | 24.7 | 15.0 | 7.90 | 0.00 | 0.04 | 1.44 | 0.00 | 2.56 | 5.76 | 4.96 | 0.42 | 0.26 | 0.00 | 0.00 | 0.00 |
| dense-forest | 19.1 | 52.5 | 12.6 | 6.4 | 5.04 | 0.00 | 0.02 | 0.83 | 0.00 | 1.30 | 3.06 | 1.24 | 0.15 | 0.10 | 0.00 | 0.00 | 0.00 |
| dungeon | 23.9 | 41.8 | 12.5 | 11.4 | 4.53 | 0.00 | 0.02 | 0.86 | 0.00 | 0.79 | 1.37 | 3.71 | 0.21 | 0.04 | 0.00 | 0.00 | 0.00 |
| cave-roof | 4.5 | 223.7 | 4.0 | 0.5 | 2.89 | 0.00 | 0.01 | 0.06 | 0.00 | 0.06 | 0.20 | 0.01 | 0.01 | 0.00 | 0.00 | 0.00 | 0.00 |
| open-sea | 9.6 | 104.6 | 5.8 | 3.7 | 2.83 | 0.00 | 0.01 | 0.52 | 0.00 | 0.25 | 0.82 | 0.70 | 0.06 | 0.00 | 0.00 | 0.00 | 0.00 |
| walk | 10.3 | 96.7 | 6.8 | 3.5 | 3.27 | 0.00 | 0.04 | 0.68 | 0.00 | 0.27 | 1.17 | 0.69 | 0.10 | 0.03 | 0.00 | 0.00 | 0.00 |

"Render frame" is the client's own drawing (the stages are inside it);
"outside the client's counters" is the mean minus render frame and the
world update: Godot turning the canvas commands into GPU work. It grows
with the command count (31,763 at the bank).

| Scene | draw calls | batcher items | texture switches | draw commands | statics drawn | covering land drawn | mobiles drawn | items drawn | multis drawn | GPU ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| open-field | 2,496 | 632 | 98 | 8,242 | 3,381 | 952 | 44 | 30 | 0 | 0.126 |
| britain-bank | 11,929 | 2,684 | 124 | 31,763 | 18,329 | 6,847 | 102 | 612 | 0 | 0.692 |
| dense-forest | 5,449 | 583 | 904 | 18,748 | 11,449 | 2,048 | 34 | 388 | 0 | 0.323 |
| dungeon | 9,308 | 1,612 | 566 | 12,477 | 2,282 | 6,116 | 80 | 74 | 0 | 0.708 |
| cave-roof | 92 | 26 | 85 | 2,774 | 1,036 | 6 | 4 | 0 | 0 | 0.264 |
| open-sea | 3,295 | 663 | 400 | 7,211 | 2,638 | 1,070 | 16 | 2 | 0 | 0.843 |
| walk | 2,615 | 614 | 156 | 7,697 | 2,768 | 1,028 | 54 | 82 | 0 | 0.574 |

## The community's setup: 2560x1440, zoom 1.0

| Scene | mean ms | FPS | render frame ms | outside the client's counters ms | gather ms | mesh build ms | gather tail ms | land mesh ms | loose land ms | sort ms | statics ms | covering land ms | mobiles ms | items ms | multis ms | other ms | transparent ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| open-field | 1.8 | 550.6 | 1.0 | 0.9 | 0.23 | 0.00 | 0.00 | 0.06 | 0.00 | 0.04 | 0.20 | 0.07 | 0.04 | 0.00 | 0.00 | 0.00 | 0.00 |
| britain-bank | 4.4 | 229.1 | 2.9 | 1.5 | 0.76 | 0.00 | 0.01 | 0.09 | 0.00 | 0.31 | 0.66 | 0.40 | 0.08 | 0.06 | 0.00 | 0.00 | 0.00 |
| dense-forest | 3.0 | 336.5 | 2.2 | 0.8 | 0.46 | 0.00 | 0.01 | 0.04 | 0.00 | 0.30 | 0.71 | 0.09 | 0.10 | 0.06 | 0.00 | 0.00 | 0.00 |
| dungeon | 3.4 | 296.9 | 1.8 | 1.6 | 0.35 | 0.00 | 0.01 | 0.07 | 0.00 | 0.14 | 0.19 | 0.51 | 0.07 | 0.01 | 0.00 | 0.00 | 0.00 |
| cave-roof | 2.1 | 478.9 | 1.6 | 0.5 | 0.97 | 0.00 | 0.01 | 0.01 | 0.00 | 0.05 | 0.12 | 0.01 | 0.01 | 0.00 | 0.00 | 0.00 | 0.00 |
| open-sea | 1.2 | 859.1 | 0.6 | 0.6 | 0.18 | 0.00 | 0.00 | 0.03 | 0.00 | 0.00 | 0.00 | 0.00 | 0.01 | 0.00 | 0.00 | 0.00 | 0.00 |
| walk | 2.0 | 497.7 | 1.1 | 0.9 | 0.29 | 0.00 | 0.01 | 0.08 | 0.00 | 0.05 | 0.23 | 0.13 | 0.03 | 0.00 | 0.00 | 0.00 | 0.00 |

| Scene | draw calls | batcher items | texture switches | draw commands | statics drawn | covering land drawn | mobiles drawn | items drawn | multis drawn | GPU ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| open-field | 579 | 123 | 55 | 2,987 | 502 | 151 | 14 | 0 | 0 | 0.217 |
| britain-bank | 1,904 | 410 | 67 | 7,127 | 2,885 | 1,091 | 34 | 245 | 0 | 0.199 |
| dense-forest | 526 | 142 | 57 | 6,204 | 3,513 | 170 | 18 | 282 | 0 | 0.21 |
| dungeon | 2,282 | 406 | 85 | 4,632 | 411 | 1,608 | 28 | 21 | 0 | 0.402 |
| cave-roof | 55 | 21 | 48 | 2,863 | 652 | 35 | 4 | 0 | 0 | 0.201 |
| open-sea | 132 | 25 | 40 | 1,880 | 0 | 0 | 4 | 0 | 0 | 0.312 |
| walk | 672 | 172 | 54 | 3,015 | 509 | 262 | 12 | 5 | 0 | 0.133 |

## Regression or setup? Main against the 2026-09-27 commit, tonight

The PERF-01 lab run read 13.6 FPS at the bank against 18.6 in the
2026-09-27 baseline. That lab run was taken while other projects loaded the
machine to 100% CPU; quiet, main does 25.2 FPS. To compare like with like,
the commit of the baseline (cb97933e) was built optimised and run tonight
on the same shard, machine and window, minutes apart (its only local
change: the probe reads today's per-user owner password):

| Scene | Sept 27 (cb97933e) mean ms | today mean ms | change | Sept 27 draw calls | today draw calls | Sept 27 commands | today commands | Sept 27 world prepare ms | today | Sept 27 world draw ms | today |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| open-field | 5.6 | 8.1 | +44% | 1,784 | 2,496 | 7,668 | 8,242 | 1.797 | 2.632 | 1.784 | 2.534 |
| britain-bank | 24.9 | 39.8 | +60% | 8,148 | 11,929 | 28,308 | 31,763 | 5.816 | 7.959 | 9.418 | 15.419 |
| dense-forest | 11.7 | 19.1 | +63% | 3,593 | 5,449 | 17,256 | 18,748 | 3.309 | 5.072 | 4.348 | 6.708 |
| dungeon | 14.5 | 23.9 | +65% | 6,705 | 9,308 | 10,076 | 12,477 | 3.213 | 4.591 | 4.138 | 7.005 |


**Main is about 60% slower than the 2026-09-27 commit on the same content**:
a renderer regression, not the scene, the look or the build. (The
2026-09-27 baseline's 53.8 ms was most likely an unoptimised build: the
probe has rebuilt optimised only since 2026-09-28.) Draw calls rose 46% at
the bank, world draw by half. The largest single new cost is covering land
(`RenderLists.CoverFromBelow`): 6,847 land tiles drawn again a frame at the
bank and 6,116 in the dungeon, 5.0 and 3.7 ms in the client plus their share
of the engine's command processing. Two parity fixes of 2026-09-28 widened
what triggers it (8b819bb8, riverbanks: the highest drawn corner; 9104654a,
F6: past the 8-tile square). `--cover-cull` leaves 6,271 of the bank's
6,847. The object-id buffer (66499d03), the mobile body/shadow split
(71f47ae6) and the splat and terrain-layer hooks (ad97a2d0) also landed
since; **this is not bisected yet**.

## What it says

1. **The GPU is idle** (under 1 ms everywhere). Every number is CPU.
2. **Statics are most of the frame zoomed out.** At the bank 18,329 of the
   25,890 objects drawn are statics: their gather share (most of 7.9 ms),
   their sort share (most of 2.6 ms), their own draw (5.8 ms) and their 58%
   of the commands the engine then processes (about 9 of 15 ms). That is
   about 20 of 40 ms, and it is what ADR-0037 (statics on the GPU) removes.
3. **Covering land is the second cost** (6,847 tiles, 5.0 ms plus about 3
   ms of engine time). It exists only because the canvas has no depth
   buffer; with one (ADR-0037) it goes.
4. **The community's numbers fit zoom 2.5, not zoom 1.0.** Their dense city
   at 25-35 FPS matches our bank at zoom 2.5 (25.2); at zoom 1.0 we measure
   229. Their cave (14-17 FPS) is not reproduced: our crypt under land runs
   224 FPS at zoom 2.5, its cost mostly the gather (2.9 ms) of 1,036
   statics. Asking which cave would settle it.
5. **Sort is small** (2.6 ms at the bank). Sorting faster is not the win.

## Next

- Bisect the regression (four probe runs on the commits above), then fix
  or accept each cause with numbers.
- ADR-0037 with these numbers as its "before".
