# Performance count gate (PERF-02)

The gate fails when any scene has more than **5% extra draw calls** or **any
increase in batcher items**. p50, p95, p99 and mean frame times are advisory
and appear in their own table, frame times first; FPS is not shown. No FPS
improvement is implied by passing.

Since PERF-LANE-01 the gate measures only an **exported Release build**
(`--exe`), always with the **quiet fixture** (`--perf-quiet`: weather off,
incoming messages dropped), and every scene row carries the host CPU of its
sampled seconds only, apart from launch and warm-up. A report from the editor
or a debug export (`export_template` not `release`, or `engine_debug_build`
true) or without `quiet_fixture` cannot record or compare.

## Hosted CI

The `perf` job in `.github/workflows/ci.yml` runs
`python -m pytest -q tools/perf_probe`. Synthetic reports exercise pass,
regression and invalid-input results, exact threshold boundaries, advisory
timings, and the local baseline lifecycle. Its summary names the binding
signals. This verifies the gate's behavior, not game performance. The hosted
runner needs no client install, account, shard or private service. There is
no live baseline in the repository.

## Local prerequisites

Use the pinned Godot .NET engine, the .NET SDK, your own UO client install,
and a running shard where the probe account is a GM. Set paths through the
normal launcher configuration. Set **`UO_BUILD_DIR` outside the checkout**;
the gate refuses a build directory inside it. Never commit baselines, probe
logs, proprietary files, credentials or rendered captures.

Run `launchers/dev/perf_gate.bat --doctor` (`.sh` on Linux/macOS). This checks
that the configured engine and install directory exist and explains the
remaining prerequisites. It does not open a TCP connection to the shard:
bare connections can trigger server login-abuse protection. Use
`launchers/pipeline/01_verify_client_data` to verify the install. A successful
doctor is not proof that the shard is running or the account is a GM.

## Record and compare

```
launchers/dev/perf_gate.bat --fixture dev-world-v1 --exe EXPORT/GUO.exe --record-baseline
launchers/dev/perf_gate.bat --fixture dev-world-v1 --exe EXPORT/GUO.exe
launchers/dev/perf_gate.bat --fixture dev-world-v1 --exe EXPORT/GUO.exe --dry-run [--from-report REPORT.json]
```

`--exe` names a Release export (`launchers/windows/export`; a build exported
with `--export-debug` is refused by the report check). `--dry-run` launches
nothing: it checks the export, prints the probe command, and with
`--from-report` prints that report's frame-time table and whether it could
bind.

The advisory table's "other CPU" column is the host load the client did not
cause, mean/max over the sampled seconds (`host_other_cpu_sample_*_pct`).
Above 10% the row is marked `shared`: its frame times were taken on a busy
host and are not quotable as a gain or a loss.

The first command runs the optimized probe and records a new local baseline.
It refuses an existing baseline. The second runs the probe again and prints
the scene-by-scene count and timing tables. The gate pins a 1280x720 window,
zoom 1.0, Forward+ renderer, postfx off, vsync off and no frame cap by default.
`--size WIDTH,HEIGHT`, `--zoom`, `--renderer`, `--port`, and `--args` can select
another configuration. The renderer is passed to Godot before its `--`
separator. `--args` holds extra client flags and must match exactly. The
gate adds its own flags in front of them: `--perf-quiet` (see
`2026-10-24_cross_launch_pixel_variance.md`), `--perf-static-world`, so the
world is measured without mobiles (other than the player), items or
effects and a wandering NPC or a dropped loot pile cannot move a count
between two identical runs; and `--perf-scene` for its five stationary and
walk scenes, so the probe spends no time on scenes the gate does not bind
on. A probe report without `static_world: true` cannot record or compare.
`--perf-static-world` is a probe-only flag: the client refuses to start with
it and without `--perf-probe` (exit 2), and the switch is live only while
the probe runs, so ordinary play can never see a thinned world. The gate's
map-only fixture says nothing about mobiles, items, effects or play: the
perf probe without the flag and the benchmark tour (`launchers/dev/perf_tour`)
keep measuring the full world, and remain the evidence for those.

`--fixture` is your label for a stable shard world and data fixture. Change
it when the world or content changes. The tool does not freeze NPCs, verify
map hashes, or manage the shard. Keep the client data, shard state, profile,
UI and lighting consistent between measurements, and avoid concurrent heavy
work when interpreting frame times. Mobile movement, animation and world
changes may alter counts; investigate those before calling a failure a code
regression. Zoom requests are clamped by the client's camera range; choose
a supported value. Older probe JSON does not report the actual zoom.

Use `--name city-1440` for another baseline configuration. To refresh an
existing one deliberately, run the same command with `--replace-baseline`.
Only a successful, complete probe can replace it. A failed run leaves the
old baseline intact. Replacement is a local decision, never a baseline
commit. Record the reason for a refresh alongside your performance evidence.

Baselines are stored at
`<UO_BUILD_DIR>/perf/gate/<machine-tag>/<name>.baseline.json`; the machine tag
is a hash, not the computer name. Comparisons require the same machine tag,
fixture label, shard endpoint hash, client version, window, zoom, renderer
and extra flags. Each run uses a fresh label, preventing reuse of stale
reports. The baseline records what was measured (git head, dirty tree,
client DLL hash); a comparison prints the baseline head beside the
measured head so a number can always be traced to its build; the DLL hash
is the export's `data_*/GUO.dll`. The baseline embeds the probe report, and summaries are written
beside it. The raw probe JSON and log stay under `<UO_BUILD_DIR>/perf/`.
The format is specified in [section 41](../data_formats.md#41-perf-probe-and-perf-lab-reports-buildperf-toolsperf_probe-toolsperf_lab).

With `--manifest-inputs`, actual scene conditions and diagnostic modes must
also match. Missing cohort fields require a fresh baseline; matching fixture
hashes alone cannot establish comparability. Intentional optimization flag
changes use `--experiment-arm FILE` on both recording and comparison, declaring
the same flag names and each arm's expected values. This declaration never
allows minimap, window, zoom, renderer, profile, route or diagnostic drift.

Exit 0 means a comparison passed, or an explicitly requested baseline was
recorded. Recording prints that it is **not a regression comparison**. Exit
1 means counts regressed. Exit 2 means prerequisites, configuration,
measurement, or input validation prevented a comparison. Missing baselines
and settings mismatches fail before starting the client. Malformed,
incomplete, idle and unoptimized reports cannot pass.

## Smoke procedure

1. Run `--doctor`; confirm it makes no shard connection.
2. Run `python -m pytest -q tools/perf_probe`; all synthetic contracts pass.
3. With a stable fixture and a GM probe account, record a baseline using a
   new `--name smoke`, then repeat without `--record-baseline`. Inspect all
   six scenes and the advisory frame-time table.
4. Change `--zoom` without changing `--name`: it must exit 2 before launch.
5. A second `--record-baseline` must refuse to overwrite; only explicit
   `--replace-baseline` can refresh after a successful probe.

The automated tests cover the orchestration without launching Godot. A live
smoke requires the local shard and an available runtime/benchmark slot; record
whether it ran when reviewing this tool.

`tools/scenarios/client/perf_gate.scenario.json` also covers the real probe
input path through the scenario runner (`client.perf.gate`). Set
`GUO_SCENARIO_PERF_OUT` to a fresh directory outside the checkout and use the
GM dev probe account. It waits for the walk measurement and report output;
it does not compare a baseline. Use the launcher smoke steps above to test
the complete local gate.
