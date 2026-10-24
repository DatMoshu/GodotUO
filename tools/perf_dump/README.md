# tools/perf_dump

World prepare and world draw timed by each client's own profiler, ClassicUO
against GUO, in the same scenes (Epic B, B4). Each client runs once per scene
with `GUO_PERF_DUMP` set (`src/Bootstrap/PerfDump.cs`, compiled into both;
ClassicUO through `tools/render_dump/inject.targets`), jumps there with `[go`
and writes its last 60 frames' averages to `build/perf_dump/<scene>/`. No
keystrokes and no focus. Numbers compare only on one machine at one window
size and zoom. Needs a shard.

Settling accepts either the existing small transparent-object count or an
unchanged identity/alpha signature for two seconds. Permanently translucent
foliage must not force a 60-second fade timeout. Reports retain the timeout,
wait duration and `transparent_alpha_stable` result for validation. Reject
timed-out, wrong-position, wrong-viewport or unoptimized samples.

The GUO report also records `godot_api_jit_optimized`, `godot_api_mvid` and
`engine_debug_build`. Project optimization does not imply that the engine's
loaded managed API is optimized. The shared render/update contexts are CPU
measurements, not uncapped throughput or GPU time; report custom RD recording
work separately when comparing the experimental renderer.
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

For a quick GUO-only loop on an existing Release export:

```
python tools/perf_dump/run.py --only guo --guo-exe PATH_TO_EXPORT/GUO.exe --size 2560,1440 --zoom 2 --seconds 15 --sample-seconds 30 --scene britain-bank --scene britain-docks
```

This never launches or implicitly builds CUO. `--seconds` is the settling delay;
`--sample-seconds` is a separate sustained interval after fade settling. Increase
`--timeout` when requesting a long interval (up to 300 seconds). Current profile
and frame cap still apply; inspect camera, zoom, cap, position and Release/API
identity in the receipt before comparing runs.

GUO writes one-second observations to `guo-samples.jsonl` and sets
`sample_complete=true` in the final `guo.json`. The driver waits for that flag.
The first interval is about two seconds; actual monotonic intervals and draw/
process-frame deltas are saved. Overall FPS is total drawn frames divided by
total sample seconds, not an arithmetic average of per-window FPS. These are
throughput observations, not p95 individual-frame times or GPU timing. The
existing profiler phase fields still cover its latest 60 entries. Sampling and
JSON-writing overhead are part of these diagnostic runs. Preserve results before
rerunning the same output path, and record other host load separately.

`GUO_PERF_SAMPLE_SECONDS` is the opt-in client diagnostic behind this argument;
it is ignored by CUO's injected build. Custom runners must wait for completion,
use a fresh output directory and retain all samples. Without it the original
one-shot contract is unchanged.

GUO candidate-cache counters report hits, misses, builds, rejected plans and
dependency checks per frame, alongside first-Land lookup/scan/node counts.
They describe eliminated discovery work, not a promise of higher FPS.

## Tests

`python -m pytest tools/perf_dump/test_run.py` checks the driver's one-shot,
sustained-completion, partial-JSON and early-exit handling. A live run is still
required to validate location, build identity and measured output.
