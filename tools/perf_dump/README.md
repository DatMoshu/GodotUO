# tools/perf_dump

World prepare and world draw timed by each client's own profiler, ClassicUO
against GUO, in the same scenes (Epic B, B4). Each client runs once per scene
with `GUO_PERF_DUMP` set (`src/Bootstrap/PerfDump.cs`, compiled into both;
ClassicUO through `tools/render_dump/inject.targets`), jumps there with `[go`
and writes its last 60 frames' averages to `build/perf_dump/<scene>/`. No
keystrokes and no focus. Numbers compare only on one machine at one window
size and zoom. Needs a shard.

`--quiet` (GUO only) sets `GUO_PERF_QUIET=1`: the weather stays off and
incoming messages are dropped, so captures of two launches can match
(`docs/perf/2026-10-24_cross_launch_pixel_variance.md`). Receipts record
`export_template` and `engine_debug_build`; only a Release export times what
players run.

## Run

```
python tools/perf_dump/run.py [--port 2598] [--size 2560,1440] [--zoom 2.5] [--build] [--scene NAME] [--only cuo|guo]
python tools/perf_dump/run.py --report
```

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed.
