# Release covering-land handle cache

The ordinary Windows Release renderer repeatedly queried `ArrayMesh.GetRid()`
for covering-land meshes that `CoverRuns` already retained. Cache the RID in
the owning entry and refresh it after every mesh build. Cache hits now return
that handle directly. The entry still retains its `ArrayMesh`; geometry,
quads, order, shaders, atlas handling and cache eligibility are unchanged.
This is an L0 implementation optimization in the existing GUO rendering seam.
It adds no option and does not promote either experimental GPU renderer.

## Motivation and scope

A fresh 30-second Britain-bank trace used a real Windows `--export-release`
build at base commit `239bc8e9`, 2560x1440, zoom 2, cap 250. Runtime reports
confirmed optimized GUO and GodotSharp code and `engine_debug_build=false`.
CUO used its optimized Release build with the same viewport, zoom, location,
profile and cap. Clients ran sequentially; other projects remained active.

Main-thread sampled stack residence attributed 35.4% to texture-rect native
submission, 17.5% to `AddTileToRenderList`, 10.3% to `FillGameObjectList`,
6.6% to `Resource.GetRid`, and 6.5% to `DrawMeshSprite`. Almost all GetRid
samples came from `FlushCover` (1,974.5 ms of the 30-second capture).
These are sampled stack weights, including native boundaries and possible
waits, not exact method CPU durations or GPU timings.

The fresh candidate trace contains no sampled `Resource.GetRid` hotspot.
However, sampled residence in `CanvasItemAddMesh` rises from 1.8% to 7.8%,
while the rect-submission boundary remains 34.3%. This substantially offsets
the vanished GetRid attribution. Removing calls is verified; recovering the
entire earlier 6.6% as usable frame time is **not** verified. A native Godot
render-thread trace is needed to distinguish submission, synchronization and
sampling attribution before claiming a boundary-cost saving.

## Validation

The separate candidate Release export passes all 303 batcher checks. Six new
checks compare ordinary per-tile drawing with built and reused covering-run
meshes: overlapping translucent slopes, alpha rebuilds, and geometry changes
after pooled-layer reuse. All pixels match in those synthetic cases, and the
unchanged repeat builds no new mesh. This is bounded regression coverage, not
a claim of whole-client CUO pixel acceptance.

Full `launchers/dev/smoke.bat` completed with exit zero and `[smoke] OK` on
retry, with `UseSharedCompilation=false`, `MSBUILDDISABLENODEREUSE=1` and
`DOTNET_CLI_USE_MSBUILD_SERVER=0` in the child environment. The first attempt
retains its failure after the completed build's stalled console wrapper was
terminated; remaining checks completed. Existing editor Art 100/101 reporting,
optional native Pixelorama/private-shard skips and exit resource warnings
remain. No tooling source was changed to obtain the passing retry.

Both exports completed pack creation and the editor exited, but their console
wrappers remained alive without descendants. Only those verified completed
wrappers were terminated. Runtime tests use the actual exported executables;
the wrapper exit is not recorded as a clean export-command exit.

An earlier four-scene `release2-batch-diagnostic` run is excluded from sprite
batching conclusions: Windows startup selects MergedLand as PerfProbe's parity
target, and `--batched-world` does not reset that delegate. The old triangle
batching experiment remains disabled; ADR-0007 already records its measured
regression. The handle-cache checks above exercise covering land directly.

## Release comparisons under shared load

Fifteen sequential observations alternated the preserved Release control,
the candidate and CUO. Bank was repeated in reverse order; the other three
scenes have one observation per variant. Profiles, viewport, zoom, cap,
location and before/after binary hashes were checked. Host CPU samples reached
94.9%; these are observations under other projects' load, not a clean-host
or causal speedup claim.

| Scene | Control CPU ms | Candidate CPU ms | CUO CPU ms |
|---|---:|---:|---:|
| Britain bank | 7.459–9.667 | 5.554–6.326 | 4.794–4.858 |
| Dense forest | 4.013 | 4.098 | 3.782 |
| Dungeon | 3.301 | 2.138 | 2.556 |
| Britain docks | 3.772 | 4.682 | 3.410 |

CPU here is the shared profiler's `RENDER_FRAME + UPDATE_WORLD` elapsed scope,
not end-to-end FPS or GPU time. The 60-entry profiler ring is not proof of 60
newly settled frames. Prepare time, which this patch does not change, varied
substantially too. The mixed results do not establish a stable frame-rate gain
or attainment of the 250 FPS cap. No attempts were discarded. An earlier
eight-observation baseline is also retained separately, not pooled selectively.

## Next measured targets

1. `LandPages.ScaleOf` still queries native texture width and height for each
   covering tile. Atlas dimensions are documented as stable; measure caching
   those dimensions with explicit reset/lifetime tests as a separate change.
2. The ordered RD prototype still packs four vertices per occurrence and hashes
   full content each frame. Add object identities with generations and dirty
   geometry/state updates, retaining exact ordered occurrences and full fallback.
   Existing GPU residency alone does not eliminate CPU capture.
3. Separate immutable chunk geometry from visibility, alpha, roof, selection,
   animation and light changes. Preserve the tile walk's UO semantics while
   measuring each category before introducing incremental traversal.
4. Consider a native bulk submission extension only if the reduced call stream
   still shows a material managed/native boundary cost. Rewriting the client or
   switching languages does not remove redundant work by itself. Keep the
   byte/pixel oracles and measure CPU, render-thread work and GPU independently.
   In particular, resolve the native mesh-submission cost above before pursuing
   further changes based only on managed native-boundary samples.

The RD prototype remains off by default and Debug/Labs-only under ADR-0043.
It needs a separate promotion decision before it can be compared as a shipping
Release renderer. A 250 FPS target requires an end-to-end frame budget of 4 ms;
the shared profiler's CPU scopes are not a substitute for that throughput test.

Private evidence run: `20261023_guo-codex-perf-implementation-20261022_release-cpu-hotpaths`.
It retains the two sanitized traces, before/after identities, all 23 ordinary
CPU observations, host-load samples, test summaries and private reproduction
helpers. Credentials, profiles, original nettraces, client data and artwork are
excluded from the archived evidence and the repository.
