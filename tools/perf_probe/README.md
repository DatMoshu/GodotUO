# tools/perf_probe

Frame time in fixed scenes (Epic B, B1): runs the desktop client with
`--perf-probe`, windowed and never taking the focus, against a shard where the
probe account is a GM. It measures the login screen, an open field, the
Britain bank, a dense forest and a dungeon, then a run through ground loading
into view, and writes `build/perf/perf_<label>.md` and `.json`. Frame times
compare only on one machine; draw calls and batcher counts compare anywhere.
On Android, the Deck or the web run the client with the same flags.

`sample_window_version: 2` aligns cumulative counters, stage reset, allocation
and wall time after the same initial `ProcessFrame` boundary. Older reports
omit this field: their cumulative counters and first stage sample included one
extra, unmeasured interval. Compare timing stages using the same window version;
do not interpret that correction as a renderer speedup. The wall-clock sample
count itself remains unchanged.
This does not remove publication latency from existing engine monitors or
`LastFrame`/`LastKinds`, which still describe the previously published draw.

With `--perf-parity`, `parity_frames` lists all five saved PNG filenames in
capture order: plain, test, plain, test, plain. The original `_plain.png` and
`_batched.png` names remain; `_plain2.png`, `_batched2.png` and `_plain3.png`
permit independent reconstruction of stable and repeatable differences. These
are private runtime captures in the configured output folder, not test assets
to commit. Capture and PNG encoding happen outside the timed measurement.
PNG save errors fail the probe instead of publishing a list of missing evidence.

## Run

```
python tools/perf_probe/run.py [--label NAME] [--port PORT] [--args "--batched-world"] [--size 1280,720]
python tools/perf_probe/run.py --compare A B
```

## Compare counts against a baseline

```
python tools/perf_probe/gate.py --baseline <baseline-report.json> --report <measured-report.json> --summary <summary.md>
```

The comparator accepts the client's existing PerfProbe JSON format. Draw calls
may rise by **at most 5%** in each scene; batcher items may **not rise at all**.
p50, p95, p99 and mean frame times (in that order; no FPS) and the
sampled seconds' other-process CPU appear in a separate advisory table and
cannot fail the count comparison; a row above 10% other CPU is marked shared. `--summary` appends Markdown, suitable for a CI
step summary. Exit 0 means the count comparison passed, 1 means a count
regressed, and 2 means an input was invalid or incomparable.

Both documents must contain `optimized: true`, `export_template: "release"`,
`engine_debug_build: false`, `quiet_fixture: true`, matching nonempty `window`
and `postfx` strings, and a `scenes` array containing exactly one each of
`login`, `open-field`, `britain-bank`, `dense-forest`, `dungeon`, and `walk`.
Each scene needs positive finite numbers for `draw_calls`, `batcher_items`,
`p50_ms`, `mean_ms`, `p95_ms`, `p99_ms` and `draws_per_frame`; draws per frame
must be at least 0.95, and p50 <= p95 <= p99. `frames` must be an integer of
at least 360 for stationary scenes. The clock-timed `walk` needs at least
one measured frame and a positive integer `tiles_moved`. Missing, duplicate,
extra, idle or unoptimized scenes fail validation. Additional probe fields
are allowed and ignored by the comparator.

Hosted CI runs a `perf` job testing the comparator and local orchestration
with synthetic report files. It does not measure FPS or need client data,
a shard or a private runner. A login-only report cannot pass the comparator.

## Local gate

```
launchers/dev/perf_gate.bat --doctor
launchers/dev/perf_gate.bat --fixture dev-world-v1 --exe EXPORT/GUO.exe --record-baseline
launchers/dev/perf_gate.bat --fixture dev-world-v1 --exe EXPORT/GUO.exe
launchers/dev/perf_gate.bat --fixture dev-world-v1 --exe EXPORT/GUO.exe --dry-run --from-report REPORT.json
```

`--exe` (required for a measurement) is a Release export; `run.py --exe` runs
the probe on it without rebuilding. `--dry-run` launches nothing.

Use the `.sh` twin on Linux/macOS. Configure `UO_BUILD_DIR` outside the
checkout first. The launcher runs the optimized real probe with the gate's
own flags in front of yours (`--perf-static-world`, `--perf-quiet`, then `--perf-scene` for
each of its five stationary/walk scenes, so the probe measures a map-only
world and only the six scenes the gate binds on; the flag is refused without
`--perf-probe` and is live only while the probe runs), then compares
against `<UO_BUILD_DIR>/perf/gate/<machine-tag>/<name>.baseline.json`.
The baseline also records the measured head, whether the tree was dirty
and the client DLL hash; comparisons print both heads.
There is no committed live baseline. Use `--name` for separate configurations;
`--replace-baseline` deliberately replaces an existing baseline only after
a successful probe. Missing or mismatched baselines fail before launching
the client. `--doctor` never connects to the shard.

See [the gate guide](../../docs/perf/performance_gate.md) for prerequisites,
flags, smoke steps and limitations, and [section 41](../../docs/data_formats.md#41-perf-probe-and-perf-lab-reports-buildperf-toolsperf_probe-toolsperf_lab)
for the baseline contract.

## Auditable Release measurements (opt-in)

Build from a clean committed checkout with `launchers/windows/export.bat --release --manifest`.
The adjacent `.build.json` binds the source head/tree, SDK, export command,
engine/config hashes, executable, external PCK and all exported dependencies.
Missing or multiple GUO/GodotSharp assemblies fail; the normal export path is unchanged.

Pass `--manifest-inputs private-inputs.json` to this tool or the local gate.
This private JSON has nonempty `configuration` and `fixture` objects mapping
descriptive names to files (relative paths resolve beside the JSON). Include the
profile/settings and every content/route input defining the cohort. Only file
hashes are retained. This is an explicit input inventory, not automatic discovery
of all files a shard or client might read. Files must remain unchanged during the run.

The option requires a fresh label and a matching clean Release build receipt.
It enables `GUO_PERF_MANIFEST=1` and `GUO_PERF_RAW_FRAMES=1`. Runtime executable,
client and GodotSharp hashes must match the export, with optimized client/API and
a Release engine; MVIDs, engine/runtime/OS, effective render switches and actual
zoom/window are recorded. Build/config hashing and all serialization run outside
sample windows. A failed identity check cannot record a gate baseline.

Raw files contain `guo/raw-frames@1`, chronological `frame_ms`, frame count,
initial capacity, zero dropped samples and a no-growth assertion. Each scene
links its file; the measured manifest hashes it and verifies count and elapsed
time. Capacity growth, invalid durations, missing samples/files or mismatched
runtime identities fail acceptance. Existing probe counters still measure process
frames; raw durations are not new GPU measurements. Probe-overhead controls remain
required before interpreting timings.

`GUO_PERF_RAW_FRAMES=1` alone enables raw output without a manifest.
`GUO_PERF_MINIMAP=1` restores PR161's explicit open-minimap cohort; each scene
reports `minimap_open`. It does not change minimap storage or visibility timing.

The manifest gate compares actual global and per-scene window/zoom/renderer,
minimap state, profile/PostFX, warm-up/sample/route settings and diagnostic modes.
It also records and compares the minimap request. Matching input hashes do not
override changed effective state. Old reports missing these fields need a fresh
baseline; invalid comparisons and failed replacements preserve existing bytes.

To vary an optimization intentionally, pass `--experiment-arm arm.json` to the
local gate on both runs. For example, the control file can contain
`{"name":"control","flags":{"gpu_world":false}}`, and the candidate file
`{"name":"candidate","flags":{"gpu_world":true}}`. The gate requires matching
declared dimensions, verifies their actual values, and records both arms beside
the result. Their corresponding `--args=--gpu-world=off` / `--args=--gpu-world=on`
may differ. Only known switches wholly covered by the declaration are excluded
from argument equality; all other arguments and all cohort state still bind.
Environment-controlled flags such as `retained_canvas` use the same declaration.
Allowed dimensions are the eight boolean fields in `runtime_identity.effective_flags`:
`batched_world`, `merged_cover`, `merged_land`, `merged_land_ordered`,
`merged_land_array`, `gpu_world`, `retained_canvas`, `retained_cover_candidates`.
Declaring an arm does not enable it: use the existing runtime switch, and a request
that did not take effect fails. The tool does not authorize running experiments.

## Tests

```
python -m pytest -q tools/perf_probe
```

The comparator tests use synthetic numbers to check threshold boundaries,
invalid inputs, advisory timings, exit codes and summary output. Local-gate
tests mock the real probe to prove baseline creation/replacement, failed-run
preservation, settings matching and output containment. They make no claim
about renderer speed. The actual probe still needs a windowed
Godot client, a client data install and a GM account on a shard.
