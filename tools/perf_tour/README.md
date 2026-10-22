# tools/perf_tour

The benchmark tour: GUO against ClassicUO in the same places, at the same
window and zoom, in one repeatable run that writes one report. It is the
measurement behind every frame-rate claim in the perf lane: run it before a
renderer change and after, on the same machine, and read `tour.md`.

- **GUO** runs through `tools/perf_probe` (360 uncapped frames per scene,
  vsync off, optimised build) once per mode:
  - `plain`: what ClassicUO draws and nothing more (`--perf-profile plain`,
    `--merged-land=off --merged-cover=off`, post-processing off);
  - `defaults`: GUO as shipped;
  - `uoplus-max`: realtime lights with spell lights, every available UO+
    feature at 100%, the realistic water style with ripples, HD filter
    Smooth, the particle cap at 512 and the `glow` post-processing look.
- **ClassicUO** runs through `tools/perf_dump`'s mechanism (`PerfDump.cs`,
  compiled into the out-of-tree build): its own profiler's averages over its
  last 60 drawn frames at the same `[go` spots and zoom, with its draw pacing
  set to its 250 FPS cap. Its "cpu ms" is render frame plus world update, and
  1000 over that is the frame rate its CPU allows: a ceiling, because it
  paces its drawn frames, not a count of presented frames.
- **Presets** are window and zoom: `gate` 1280x720 at 1.0 (the perf gate's
  own), `community` 2560x1440 at 1.0 (what the community reports), `far`
  2560x1440 at 2.5 (the camera's maximum, where the sorted world costs most).

Scenes are the perf probe's: open-field, britain-bank, dense-forest, dungeon,
britain-docks, trinsic-bay, cave-roof, open-sea, plus GUO's login and walk.

## Run

```
launchers\dev\perf_tour.bat --label before
launchers\dev\perf_tour.bat --preset far --mode plain --no-cuo        one GUO run, zoomed out
launchers\dev\perf_tour.bat --report build\perf_tour\before           redraw the report
python tools/perf_tour/run.py --help
```

Output under `build/perf_tour/<label>/`: `tour.md` (one table per preset:
ClassicUO's cpu ms and fps ceiling, then per GUO mode the mean ms, fps, p99
and draw calls), `tour.json` (the same plus batcher items, texture switches,
stage times and GPU ms per scene), `tour.png` (the fps table as one image for
a chat card), and every perf_probe and perf_dump file the tour read.

Needs the shard with the probe account as a GM and the Godot runtime lease
taken `--benchmark --exclusive`: nothing else may draw during a tour, and
frame times compare only on one machine. A full tour (three presets, three
modes, ClassicUO at eight scenes each) takes about an hour. Copy the report
into the evidence run; never commit it.

## Tests

```
python -m pytest -q tools/perf_tour
```

The tests cover the report builder on synthetic probe and dump files (the
table, the ClassicUO cpu frame, missing scenes) and the launcher lint. The
tour itself needs a shard and both clients, so it is not run in CI.
