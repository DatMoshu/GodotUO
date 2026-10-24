# Sprite runs on MultiMesh (`--multimesh-sprites`, off by default)

Date: 2026-10-24. Branch `work/perf-multimesh-sprites-20261024`, stacked on
the perf integration branch (PR 130). Stories PERF-MM-01 to 04.

**Verdict.** The path works and is pixel-identical to the canvas path. At
the bank it takes the per-sprite RenderingServer calls from about 13,300 to
about 1,900. In a Release export the time saved is small: about 0.3 ms of
world draw at the bank, nothing measurable at the docks, and no frame-time
change that holds up against run-to-run variation on the shared host. Keep
it off. The reason, and what would change it, are under
[Limits](#limits-and-what-is-not-covered).

## Why

The Release bank profile (2560x1440, zoom 2) put one call at a third of the
main thread: `CanvasItemAddTextureRectRegion`, one managed-to-native crossing
per sprite from `UltimaBatcher2D.AddSprite`, about 16,000 a frame. Statics
reach it too: `DrawStaticMeshSprite` reads a cached quad and calls
`AddSprite`. ADR-0007's triangle-array runs replaced that call with one per
run but paid for marshalling four managed arrays per run, and lost. A
MultiMesh takes one float buffer by span.

## What it does

`UltimaBatcher2D.MultiMesh.cs` holds the path; the core file has one-line
hooks, each marked `PORT DEVIATION (GUO): --multimesh-sprites`.

- **Runs.** While the switch is on, an upright sprite (AddSprite's rect
  path) and a transformed rect (TryAddAffineQuad: shadows, mirrors,
  rotations, sitted sections), drawn under the default blend into an item
  with the instancing material, join a pending run instead of being sent. A
  run is one texture in one canvas item. It ends where a `--batched-world`
  run ends: a texture change, a new item (every `Cut`), any other command
  on the item, `End`. `FlushQuadRun` flushes it, and every other draw path
  already reaches `FlushQuadRun` (through `FlushRun`, `NewItem`, or before
  covering land is held back), so submission order is unchanged.
- **Instances.** 16 floats each: a Transform2D placing the unit quad (the
  rect's size, or the affine map times the texel region, in the basis), the
  colour slot carrying the packed (hue, mode, alpha) modulate exactly as the
  rect command carried it (ADR-0002), and the custom slot carrying the source
  rect in UVs, rounded as the server rounds a rect's texel source (texels,
  then times 1/size).
- **Two or three calls a run.** `MultimeshSetBuffer` (a span over a pooled
  buffer), `MultimeshSetVisibleInstances` (only when that pooled MultiMesh's
  count changes), `CanvasItemAddMultimesh`.
- **Short runs replay.** A run of fewer than 8 sprites
  (`GUO_MULTIMESH_MIN_RUN`) is sent as the exact commands it stood in for. A
  MultiMesh pays a buffer copy, an upload and a draw call of its own, where
  consecutive rects share the canvas renderer's batch. Without this, the
  bank's ~900 runs raised draw calls 12% and cost back what the instancing
  saved. Minimums of 4, 8 and 16 measured alike; 8 is kept.
- **Pool.** `MultimeshSetBuffer` only takes a whole buffer, so a run goes into
  the smallest pooled MultiMesh holding it: capacities 16 to 4096 by powers
  of two, one pool each, reused from the start every frame (`BeginFrame`),
  grown by adding entries, never resized. A run over 4096 splits in order.
  Each MultiMesh has a fixed custom AABB so the server does not rebuild a
  bound from every buffer.
- **Quad and shader.** The unit quad is split on the diagonal the server's
  rect command uses (TL-BR, in its vertex order). Split the other way, 53
  pixels at 0.5 zoom resolved texel ties differently.
  `uo_hue_multimesh.gdshader` is `uo_hue.gdshader` plus one line in
  `vertex()`: when `INSTANCE_CUSTOM.z != 0` the quad's UV is mapped onto the
  instance's rect. While the switch is on it is the default sprite material
  (`SpriteMaterial`, used where `Begin` and `SetBlendState` chose the plain
  material), so whatever keeps its own command draws into the same item with
  `INSTANCE_CUSTOM` zero and no extra cut. It is built on first use from the
  plain material's uniforms; `SetOnAllMaterials` keeps it in step.
- **Unchanged paths.** Quads no affine map fits (triangle lists), custom and
  additive blends (their own materials), the id mirror (ADR-0023),
  `--gpu-world`'s capture, `--batched-world`, covering land, land meshes,
  splats and layers.
- **Off.** `SpriteMaterial` returns the plain material, neither append is
  called, and `FlushMultiMeshRun` returns at once: the old commands in the
  old order.

Switch: `--multimesh-sprites` (also `=on`, `=off`) or
`GUO_MULTIMESH_SPRITES=1`. The flag makes it the perf probe's parity toggle.

## Counters

The perf probe's report gains `multimesh_sprites`, `mm_instances`,
`mm_runs`, `mm_calls` (RenderingServer calls the runs made), `mm_fallbacks`,
`mm_replayed`, `mm_min_run`, `p50_ms`, `warmup_s` and `sample_s`
(data_formats section 41). `cmd_rects` and `cmd_affine_rects` keep counting
the per-sprite commands that remain. The new probe options `--perf-warmup-s`
and `--perf-sample-s` time a standing scene by the clock after an unmeasured
warm-up.

## Proof

- **Batcher probe** (`launchers/dev/batcher_probe.bat`, no shard). One scene
  of nine sprites on two textures (hued, unhued, translucent, overlapping,
  one mirrored, one rotated) is drawn per sprite and through the new path,
  and the two are compared pixel for pixel at six views: zoom 1; 1.37 with a
  fractional offset; 0.5; 2 at half a pixel; 0.8; 0.66. It runs once with
  every run instanced and once with every run replayed: 36 checks, all
  identical, counts as expected. The rest of the probe (hue, blends, land
  light, gump clip, fonts, splats) passes.
- **Frozen world in game** (`--perf-parity`, 2560x1440, zoom 2, static
  world, optimised build). Five frames per scene (plain, test, plain, test,
  plain) at login, open field, Britain bank, dense forest, dungeon and
  Britain docks. Repeatable differing pixels ("strict"): **0 in every
  scene**, on the first version and again on the final code. Pixels that
  differed in the first test frame only: 0 to 25 per scene, and 442 at the
  bank on the final run. The second test frame matched the plain frames
  there, so these are transient differences, not the path's.
- The id mirror keeps the per-sprite path: the switch never instances while
  the mirror is on. Picking is unchanged by construction; it was not re-run
  here.
- Build, `tools/render_sort_tests` (310,580 checks) and smoke: see the PR.

## Measurements

Host: Ryzen 7 7800X3D, RTX 4090, Windows, Forward+. The machine was shared
(other projects' agents running); host CPU was recorded for each run. **No
number below is a claimed FPS gain**: frame times moved within the
run-to-run spread of the shared host.

Counts per frame, final code, 2560x1440 zoom 2, static world:

| scene | rect calls off (+ transformed) | on: rects (+ transformed) | runs | instances | replayed | draw calls off -> on |
|---|---:|---:|---:|---:|---:|---|
| open field | 4,084 (+590) | 380 (+19) | 116 | 4,473 | 399 | 427 -> 432 |
| Britain bank | 12,958 (+361) | 1,046 (+52) | 392 | 12,398 | 1,098 | 1,574 -> 1,596 |
| dense forest | 10,557 (+398) | 115 (+6) | 208 | 10,937 | 121 | 470 -> 474 |
| dungeon | 3,087 (+17) | 1,156 (+16) | 24 | 2,095 | 1,167 | 1,007 -> 1,007 |
| Britain docks | 11,016 (+92) | 1,467 (+63) | 349 | 9,791 | 1,530 | 1,562 -> 1,577 |

At the bank the batcher's RenderingServer calls for sprites go from about
13,300 to about 1,900: 1,046 rects, 52 transformed rects at three calls
each, and 392 runs at two. The review expected about 100. Covering land
prevents that. Each run of covering tiles is its own item under the
land-array material, so at the bank and docks the sorted list switches
between sprite items and cover items about 800 times a frame, and a run
cannot cross an item. The forest, where covering land is rare, has the
shape the review expected: 10,557 calls become 208 runs and 121 rects.

**Release export** (Windows `--export-release`; `GUO.dll` SHA256
`798170ce3275f378e9cd8de85cd326761c07a1efb38936052e17bf61e509194e`; the
switch is read at run time, so one build serves both arms). 2560x1440, zoom
2, uncapped, vsync off, static world, 60 s unmeasured then 30 s timed per
scene, four processes in the order off, on, off, on:

| run | host CPU mean / max | bank mean / p50 / p95 / p99 ms | bank world prepare / draw ms | docks mean / p50 / p95 / p99 ms | docks world prepare / draw ms |
|---|---|---|---|---|---|
| 1 off | 34% / 64% | 9.71 / 8.99 / 15.38 / 18.10 | 3.75 / 3.15 | 7.52 / 7.03 / 10.57 / 13.39 | 2.61 / 2.40 |
| 2 on | 44% / 99% | 12.28 / 11.50 / 18.64 / 21.97 | 5.19 / 3.55 | 9.10 / 8.39 / 15.58 / 19.08 | 3.54 / 2.59 |
| 3 off | 34% / 60% | 9.51 / 9.03 / 13.72 / 17.33 | 3.71 / 3.11 | 7.33 / 6.62 / 10.85 / 14.86 | 2.53 / 2.37 |
| 4 on | 36% / 79% | 9.32 / 8.80 / 14.13 / 18.40 | 3.75 / 2.78 | 8.24 / 7.69 / 13.12 / 17.15 | 3.09 / 2.40 |

Run 2 caught a load spike: host CPU reached 99%, and world prepare, which
this path does not touch, rose 1.4 ms. Treat it as contaminated. Runs 3 and
4 had the same load and the same bank prepare time. In that pair, the bank's
world draw fell 0.33 ms (3.11 to 2.78, 11%). Its mean frame fell 0.19 ms,
within noise, and its p95 and p99 rose by about as much. The docks show no
world-draw change (2.37 to 2.40). Inside world draw the statics stage shrinks
(bank 2.20 to 1.54 ms) and the covering-land stage grows (0.39 to 0.75 ms),
because that is where a pending run is flushed.

Earlier rounds on the optimised editor build (not an export) showed the same
pattern. The first version (no transformed rects, no replay) was flat to
worse: bank world draw 4.46/4.28 ms off against 4.47/3.82 ms on, draw calls
+12%. The final version took about 0.5 ms off world draw at the bank and
docks (bank 4.27/3.76 ms off against 3.57/3.51 ms on).

## Limits and what is not covered

- **The gain is small where it was meant to be large.** In a Release export
  the per-sprite call costs less than the review's profile implied, and at
  the bank and docks covering land splits the sorted list into short runs.
  Measured here: about 0.3 ms of world draw at the bank in Release, nothing
  at the docks, and no frame-time change that survives the shared host. That
  is not a case for turning the switch on by default.
- **What would make it pay:** sprite runs that cross covering land. Either
  covering tiles become instances under the same material (they are
  stretched quads with per-vertex light, so not unit-quad instances as they
  stand), or the cover pass stops cutting the sprite item. Both touch the
  draw-order design (ADR-0004), and each needs its own parity proof.
- Not measured: the pinned bank-to-docks walk; scenes with mobiles and items
  (the static world leaves them out); the Mobile and Compatibility
  renderers; Android, the Deck and the web build. The id mirror path is
  unchanged but was not re-run with post-processing on.
- The probe's parity rows compare the switch inside one process. The Release
  timings compare whole processes, and process-to-process spread on this
  host (0.2 to 0.8 ms in mean frame time) is larger than the effect.
