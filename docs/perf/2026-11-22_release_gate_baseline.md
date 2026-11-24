# Release performance-gate baseline, 2026-11-22

First local baseline for `launchers/dev/perf_gate` on this machine (sprint guo-1122-mvp, M8).
It is a baseline, not a regression comparison: later runs of the same name compare against it.

## Run

| | |
|---|---|
| Build | Windows Release export (`launchers\windows\export.bat --release`) of integration `ba77c6e8`; the gate recorded the checkout head `61732f68` (later commits add only scenario tooling) |
| Command | `launchers\dev\perf_gate.bat --fixture dev-world-v1 --exe <export>\GUO.exe --record-baseline` |
| Shard | dev shard, ModernUO 9782fd6 (.NET 11) with the GUO patches, 127.0.0.1:2593 |
| Machine | Windows, NVIDIA GeForce RTX 4090 |
| Window | 1280x720, zoom 1.0, vsync and frame cap off, 360 frames per scene after 180 to settle |
| Profile | lights classic, water lighting, ripples and wakes 65%, shallow shores 20%, post-processing Classic |

## Frame times (ms)

| Scene | p50 | p95 | p99 | max | draw calls | FPS |
|---|---:|---:|---:|---:|---:|---:|
| login | 0.79 | 4.15 | 5.22 | 6.68 | 46 | 712 |
| open-field | 0.98 | 4.07 | 5.93 | 7.79 | 123 | 704 |
| britain-bank | 2.07 | 4.59 | 5.28 | 8.92 | 230 | 398 |
| dense-forest | 1.33 | 3.65 | 4.58 | 5.56 | 115 | 610 |
| dungeon | 1.23 | 3.77 | 4.88 | 6.60 | 214 | 616 |
| walk | 1.23 | 5.44 | 9.00 | 22.50 | 158 | 515 |

## Caveats

- Other processes kept the host busy during the run (other-CPU mean 95-98%), so tail times are noisier
  than on an idle machine. Re-record with `--replace-baseline` on a quiet machine before treating a
  small p95/p99 change as a regression.
- `walk` max 22.5 ms comes with 9 atlas page uploads during the walk.
