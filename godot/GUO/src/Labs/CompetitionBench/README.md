# Labs: CompetitionBench (task 1155)

A frozen, offline, common GPU benchmark for the render competition. Labs (L4):
compiled only with `-p:GuoLabs=true` in **Debug**, never in an export. Every
number from it is labelled Debug GuoLabs, never Release.

## Build and run

```text
dotnet build godot\GUO\GUO.csproj -c Debug -p:GuoLabs=true -nologo -v:q
launchers\dev\competition_bench.bat --label L --out <external evidence run folder> --mode capture --variants enhanced-off classic
launchers\dev\competition_bench.bat --label L --out <folder> --builds common=<checkout> entrantA=<worktree> --variants enhanced-off terrain --baseline <frozen common run>
```

`tools/competition_bench/run.py` builds nothing, launches one Godot process per
(round, build, scene, time, variant), and writes `summary.json`. The probe
(`CompetitionBenchProbe.tscn`) is offline: isolated workspace, settings, profile
and cache under `GUO_BENCH_OUT` (external, rooted, outside the repo), no account,
no shard. It refuses to overwrite an existing result for the same label.

## Frozen contract

- Window 1920x1080, camera zoom 1, roofs drawn, no injected items, player fixed.
- Scenes (`CompetitionBenchScenes.cs`, the one table): `town-roofs`, `sloped-terrain`, `forest-edge`.
  Times: `noon` 12:00, `dusk` 17:15. The sun clock (ShardClock) is pinned every frame.
- Variants: `classic` (classic lighting); `enhanced-off` (enhanced lighting, sun shadows on, lamp
  shadows 0, soften 0, weather/fog/haze/reflections/window light off, wind 0, every competition
  addition off); `terrain`, `terrain-cloud-wind` = enhanced-off plus
  `CompetitionVariants.Apply`. Base returns false: the probe records `unsupported`, exits 0.
- Admission: the requested lighting mode must really be active, sun shadows ready (enhanced),
  zero Depth World fallback frames; otherwise `[competition-bench] FAIL reason`.
- Real frame loop (normal `_Process` draw), vsync off, `Engine.MaxFps = 0`. The renderer must be `forward_plus` on
  `vulkan` at 1920x1080 or the run FAILs (re-asserted at sample start and end, with zoom 1 and vsync/MaxFps).
  Equal workload: warmup (default 1200) and sample (default 18000, about 36-54 s wall at 2-3 ms/frame) are FIXED COUNTS
  OF SIM FRAMES (1/60 s each): `GUO_BENCH_WARMUP_FRAMES` / `GUO_BENCH_SAMPLE_FRAMES`. Steady state is wall-clock:
  `GUO_BENCH_PRECONDITION_S` (default 20) of normal rendering before each process's first warmup, and
  `GUO_BENCH_REWARM_S` (default 5) between repeats, each followed by a sim-clock rebase and then the frame-count
  warmup, so the warmup and measured frames are identical to a run without those windows. Measured wall seconds
  per window are recorded (`precondition_wall_s`, per repeat `warmup_wall_s`, `preceding_rewarm_wall_s`, `wall_s`);
  the total wall time is also bounded by `GUO_BENCH_TIMEOUT_S` (exit 98). Capture mode skips the precondition.
- Deterministic simulation: `CompetitionBenchClock` steps game time exactly 1/60 s per rendered
  frame (Time.Ticks / Time.Delta), from process start, rebased to a fixed second at every sample
  start. A faster FPS cannot traverse a different wind/cloud/weather workload. Entrant wind and
  cloud animation MUST read `Time.Ticks` (or the clock), never wall time. Capture mode renders
  the still at sim frame 600 after the base. Inert when the bench is not running.
- GPU time (`gpu_ms`) = raw RenderingDevice timestamps around the whole
  `OrderedWorldCapture.RenderThread` span (world capture, light, shadow, soften, presentation;
  Vulkan nanoseconds) + `ViewportGetMeasuredRenderTimeGpu`, both read on the render thread at the same
  resolution (one frame id), a frame late, with no fence or readback. A frame with a missing or mis-ordered
  pair, or a viewport reading that is not above zero, is INVALID: counted in `gpu_coverage`, never zero.
  Every repeat is gated: valid fraction >= 99%, all pass timestamps inside the pre-draw span, median of
  (predraw+viewport)/whole-frame span within 0.93..1.07, resolved frames >= 95% of sampled frames, the viewport
  set unchanged, zero Depth World fallback. Any gate failure FAILs the run. `stampedFrames`/`unavailable` are per repeat.
  GPU clocks, temperature and power state are recorded per repeat via nvidia-smi (`unavailable` if absent).
- Hook containment: every Profile instance field (including private ones, recursively for nested values) is snapshotted
  before and after `CompetitionVariants.Apply` (and at sample start/end); anything changed that is not in
  `CompetitionVariants.AllowedFields` FAILs, and `AllowedFields` may only name NEW members: any name in the frozen list
  of Profile members at the common commit (`CompetitionBenchFrozen.cs`) is refused. The diff is in `bench.json`.
  The fixture also asserts `Engine.TimeScale == 1`, the project physics tick rate, and that the root viewport's
  scaling, MSAA and screen-space AA are unchanged since before the hook.
- nvidia-smi is sampled asynchronously inside warmup with a hard timeout; a graphics-clock drift above 15% between
  before and after sets `gpu_clock_drift_flag`. Drift is recorded for every repeat; any drifted repeat makes the score
  PROVISIONAL (uncertified), and a drifted result needs a bounded rerun or paired-control evidence before certification.

## Outputs

`data/<label>/<scene>-<time>-<variant>/bench.json` (timed) or `capture.json` plus
`stills/<label>-<scene>-<time>-<variant>.png` (capture; sha256 of the pixels). Format: docs/data_formats.md.

## Scoring (runner)

The control (`--baseline-build`) must be at the pinned `--common-sha`; its DLL sha256 is recorded, and the score is
INVALID if the control's enhanced-off differs from the frozen baseline by more than max(noise threshold, 0.3 ms).
Each entrant's own enhanced-off minus the control is reported; a negative value beyond noise is flagged
'shared-path speedup: review'. A score is also PROVISIONAL when another key's added + noise exceeds the worst key's added.
Coordinator step: rebuild the checked source of each entry yourself (Debug GuoLabs) and pass the resulting DLL hash with
`--expect-dll NAME=SHA256`, so the scored build is the checked source.

Primary: ONE session runs the common build and every entry interleaved (`--builds common=... e1=... e2=...`,
seeded shuffle per round, Latin-square-like over builds; seed and order recorded) and scores each entry against
the `common` build's enhanced-off from that same session (`--baseline-build common`). The frozen cross-session
baseline (`--baseline-label` or `--baseline-summary FILE`, sha256 recorded, never an implicit file) is reported
as a sanity delta. Never an entrant's own baseline.
Added = median of repeat medians (entry) - baseline, per scene/time; penalty 0 up to 2.5 ms, linear 0..10
points to 4.0 ms, DQ above 4.0 ms. A score is INVALID unless every planned scene/time has valid samples on both
sides, every planned process passed, and adapter/driver/method/Debug GuoLabs match. Noise = max-min of repeat
medians (unknown with one repeat, which refuses to certify a pass or a DQ: UNCERTIFIED); unstable above
max(0.3 ms, 10%). PROVISIONAL (a DQ pending a bounded rerun) is decided by the noise gate of the worst,
DQ-driving key only. Entrant `enhanced-off` and `classic` stills must match the frozen common stills
pixel-exactly on the same GPU: `run.py --offparity FROZEN_STILLS ENTRANT_STILLS`. The dusk classic light
level (20) is used only for classic still parity, never for scoring.

## Exception route

`project.godot` and `GUO.csproj` are checked byte-identical to the common commit. An entrant that genuinely needs a change
to either asks the lead; if the lead approves, the lead records an allow-list (file, hunk and reason) in the run packet,
and the coordinator checks the entrant against that allow-list instead of the blanket identity check.

## What entrants may edit

`CompetitionVariants.cs` is the benchmark integration file. It ONLY maps variant names to option
switches (and declares them in `AllowedFields`). Entrants may edit production renderer code within their own scope; the harness (probe,
clock, markers, scenes), runner, fixtures and rubric are immutable to them. A variant must leave
everything else equal to enhanced-off. The two Labs-guarded seams outside this folder
(`GameController.Update` sim step, `OrderedWorldCapture.RenderThread` markers) are part of the harness.
