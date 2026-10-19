# tools/perf_probe

Frame time in fixed scenes (Epic B, B1): runs the desktop client with
`--perf-probe`, windowed and never taking the focus, against a shard where the
probe account is a GM. It measures the login screen, an open field, the
Britain bank, a dense forest and a dungeon, then a run through ground loading
into view, and writes `build/perf/perf_<label>.md` and `.json`. Frame times
compare only on one machine; draw calls and batcher counts compare anywhere.
On Android, the Deck or the web run the client with the same flags.

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
Mean, p95 and p99 frame times appear in a separate advisory table and cannot
fail the count comparison. `--summary` appends Markdown, suitable for a CI
step summary. Exit 0 means the count comparison passed, 1 means a count
regressed, and 2 means an input was invalid or incomparable.

Both documents must contain `optimized: true`, matching nonempty `window`
and `postfx` strings, and a `scenes` array containing exactly one each of
`login`, `open-field`, `britain-bank`, `dense-forest`, `dungeon`, and `walk`.
Each scene needs positive finite numbers for `draw_calls`, `batcher_items`,
`mean_ms`, `p95_ms`, `p99_ms` and `draws_per_frame`; draws per frame must be
at least 0.95, and p95 must not exceed p99. `frames` must be an integer of
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
launchers/dev/perf_gate.bat --fixture dev-world-v1 --record-baseline
launchers/dev/perf_gate.bat --fixture dev-world-v1
```

Use the `.sh` twin on Linux/macOS. Configure `UO_BUILD_DIR` outside the
checkout first. The launcher runs the optimized real probe, then compares
against `<UO_BUILD_DIR>/perf/gate/<machine-tag>/<name>.baseline.json`.
There is no committed live baseline. Use `--name` for separate configurations;
`--replace-baseline` deliberately replaces an existing baseline only after
a successful probe. Missing or mismatched baselines fail before launching
the client. `--doctor` never connects to the shard.

See [the gate guide](../../docs/perf/performance_gate.md) for prerequisites,
flags, smoke steps and limitations, and [section 41](../../docs/data_formats.md#41-perf-probe-and-perf-lab-reports-buildperf-toolsperf_probe-toolsperf_lab)
for the baseline contract.

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
