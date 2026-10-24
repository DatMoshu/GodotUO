# Sprite runs on MultiMesh (`--multimesh-sprites`, off by default)

Date: 2026-10-24. Branch `work/perf-multimesh-sprites-20261024`, stacked on
the perf integration branch (PR 130). Stories PERF-MM-01 to 04.

## Why

The Release bank profile (2560x1440, zoom 2) put one call at a third of the
main thread: `CanvasItemAddTextureRectRegion`, one managed-to-native crossing
per sprite from `UltimaBatcher2D.AddSprite`, about 16,000 a frame. Statics
reach it too: `DrawStaticMeshSprite` reads a cached quad and calls
`AddSprite`. ADR-0007's triangle-array runs replaced that call with one per
run but paid for marshalling four managed arrays per run, and lost. A
MultiMesh takes one float buffer by span.

## What it does

`UltimaBatcher2D.MultiMesh.cs` holds the whole path; the core file has eight
one-line hooks, each marked `PORT DEVIATION (GUO): --multimesh-sprites`.

- **Runs.** While the switch is on, an upright, unmirrored sprite with a
  positive size, drawn under the default blend into an item that has the
  instancing material, is appended to a pending run instead of being sent.
  A run is one texture in one canvas item. It ends where a
  `--batched-world` run ends: a texture change, a new item (every `Cut`),
  any other command on the item, `End`. `FlushQuadRun` flushes it, and
  every other draw path already reaches `FlushQuadRun` (through `FlushRun`,
  `NewItem`, or directly before covering land is held back), so submission
  order is unchanged.
- **Instances.** 16 floats each: a Transform2D placing the unit quad
  (width and height in the basis, the corner in the origin), the colour slot
  carrying the packed (hue, mode, alpha) modulate exactly as the rect
  command carried it (ADR-0002), and the custom slot carrying the source
  rect in normalised UVs.
- **One run, three calls.** `MultimeshSetBuffer` (span over a pooled
  buffer), `MultimeshSetVisibleInstances` (only when the pooled MultiMesh's
  count changes), `CanvasItemAddMultimesh`.
- **Pool.** `MultimeshSetBuffer` only takes a whole buffer, so a run goes
  into the smallest pooled MultiMesh holding it: capacities 16 to 4096 by
  powers of two, one pool each, reused from the start every frame
  (`BeginFrame`), grown by adding entries, never resized. A run over 4096
  sprites splits in order. Each MultiMesh has a fixed custom AABB so the
  server does not rebuild a bound from every buffer.
- **Shader.** `uo_hue_multimesh.gdshader` is `uo_hue.gdshader` plus one
  line in `vertex()`: when `INSTANCE_CUSTOM.z != 0` the unit quad's UV is
  mapped onto the instance's rect. While the switch is on it *is* the
  default sprite material (`SpriteMaterial`, used where `Begin` and
  `SetBlendState` chose the plain material), so the sprites that keep the
  per-sprite path draw into the same items under it with `INSTANCE_CUSTOM`
  zero and no extra cuts. It is built on first use from the plain
  material's uniforms; `SetOnAllMaterials` keeps it in step.
- **Fallbacks, unchanged.** Rotated or mirrored sprites and shadows (the
  quad and affine paths), custom and additive blends (their own
  materials), the id mirror (ADR-0023), `--gpu-world`'s capture,
  `--batched-world`, covering land, land meshes, splats and layers.
- **Off.** The switch off is the old code path: `SpriteMaterial` returns
  the plain material, `TryAppendMultiMesh` is never called, and
  `FlushMultiMeshRun` returns at once.

Switch: `--multimesh-sprites` (also `=on`, `=off`) or
`GUO_MULTIMESH_SPRITES=1`. The flag makes it the perf probe's parity toggle,
so `--perf-parity` compares it frame by frame.

## Counters

The perf probe's report gains `multimesh_sprites` (the switch),
`mm_instances` (sprites drawn as instances), `mm_runs`, `mm_calls`
(RenderingServer calls the runs made) and `mm_fallbacks` (`AddSprite` calls
that kept the per-sprite path while the switch was on, UI included).
`cmd_rects` keeps counting the per-sprite rect calls that remain.

## Proof

PENDING

## Measurements

PENDING

## Limits and what is not covered

PENDING
