# tools/competition_bench

Runner for the frozen competition GPU benchmark (Labs feature `godot/GUO/src/Labs/CompetitionBench`,
read its README first). Launcher: `launchers/dev/competition_bench.bat` / `.sh`.

```text
python tools/competition_bench/run.py --label L --out <external folder> --mode capture --variants enhanced-off classic
python tools/competition_bench/run.py --label L --out <folder> --builds common=<path> entrant=<path> --variants enhanced-off terrain --rounds 3 --baseline <frozen common run>
python tools/competition_bench/run.py --score <run folder> --baseline <frozen common run>
python tools/competition_bench/run.py --offparity <frozen stills> <entrant stills>
```

It builds nothing (it prints the Debug GuoLabs build command), refuses an in-repo `--out`, and runs one Godot process per
(round, build, scene, time, variant). Order is a seeded shuffle per round, Latin-square-like over builds; seed, order,
UTC start times, wall and startup seconds, per-build git head and DLL sha256 go in `summary-<label>.json`. Warmup and
sample are fixed counts of simulation frames (`--warmup-frames`, `--sample-frames`, `--rewarm-frames`), never seconds.
The child process gets a scrubbed environment (allow-list + `GUO_BENCH_*` + client data/version) and
`--rendering-method forward_plus --rendering-driver vulkan`. Exit codes: 97 probe missing, 98 timeout.
Scoring flags: `--common-sha` pins the control build, `--expect-dll NAME=SHA256` verifies the coordinator's rebuild.

```text
python tools/competition_bench/run.py --label L --out <external folder> --builds common=<path> e1=<path> e2=<path> --variants enhanced-off terrain --rounds 3 --repeats 3 --baseline-build common [--baseline-label FROZEN]
python tools/competition_bench/run.py --score <summary file> --baseline-build common
python tools/competition_bench/run.py --check-entrant <common-sha> <entrant checkout>
```

`--offparity` pairs stills by the part of the filename from the scene on (`<scene>-<time>-<variant>`), ignoring the
label/build prefix, for `enhanced-off` and `classic` stills only: `fp1-common-town-roofs-noon-classic.png` pairs with
`fp2-common-town-roofs-noon-classic.png`. Keep one set per folder; any unpaired still or one differing pixel fails.
Before launching, each build's `GUO.dll` must exist, contain the probe, and be newer than its newest `.cs` source.
`python -m pytest tools/competition_bench` tests the scoring, ordering and validity rules.

## Entrant checklist (reviewer)

1. Harness, runner, fixtures, seams and docs are byte-identical to the common commit: `run.py --check-entrant <sha> <path>`
   (probe, clock, scenes, frozen members, tscn, READMEs, launchers, project.godot, GUO.csproj, docs section 53, whole seam
   blocks with PreDrawBegin first in RenderThread and PreDrawEnd right before RecordTicks). No other file may exist in
   `Labs/CompetitionBench/`; `CompetitionVariants.cs` is scanned for banned APIs (Engine, DisplayServer, RenderingServer, OS,
   Time, Environment, reflection, Task/Timer/Thread, nodes).
2. Bench symbols (`CompetitionBench`, `BenchGpuMarkers`, `GUO_BENCH_`) appear nowhere outside the allowed files (same command).
3. Only `CompetitionVariants.cs` maps variants; its `AllowedFields` lists every Profile field it switches; `bench.json`
   `profile_diff` shows nothing else changed.
4. Entrant `enhanced-off` and `classic` stills equal the frozen common stills pixel-exactly (`--offparity`).
5. Entrant wind/cloud animation reads `Time.Ticks` (the bench clock), never wall time or its own timers.
6. Scored in ONE interleaved session against the common build's enhanced-off, on forward_plus/vulkan/1920x1080, Debug GuoLabs.
7. Every repeat passed its GPU gates (valid frames, coverage, fallback 0); the score is not INVALID or UNCERTIFIED.

**GPU clock drift.** The load is light (about 0.3 ms of GPU per frame on the 4090), so graphics clocks wander and drift above 15% is normal. Drift is recorded per key; it makes a score PROVISIONAL only when the worst key is within 0.5 ms of the 2.5 or 4.0 ms edge.
