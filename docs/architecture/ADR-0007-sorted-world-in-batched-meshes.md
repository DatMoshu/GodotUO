# ADR-0007: The Sorted World Drawn in Batched Meshes (proposal)

## Status

Proposed. **Recommendation: Reject**, measured 2026-09-27 (Epic B, B2; see
"Measured" below). It stays Proposed until the owner signs off.

**Amended 2026-10-19; accepted 2026-10-22 (owner)** (below): the alternative
listed here, a depth buffer instead of the sort, is accepted for the world
pass, with the statics kept on the GPU. The original batching proposal above
retains its own status.

Written up overnight 2026-09-26 so the owner could decide; see
`docs/parity_2026-09-26_night.md`, P1.

## Measured (2026-09-27, B2)

**What was run:**

- The prototype: `--batched-world` (`UltimaBatcher2D.BatchedWorld`, off by
  default). It builds step 2 of the proposal without step 1: consecutive
  quads on one texture in one canvas item go to Godot as one
  `canvas_item_add_triangle_array` per run instead of one call per sprite.
  Rotated, mirrored and shadow quads join the runs too.
- The comparison: `launchers\dev\perf_probe.bat` (B1) in five scenes, plain
  and batched, on the desktop.
  - Machine: RTX 4090.
  - Settings: vsync, the frame cap and upstream's draw pacing lifted, 360
    frames per scene.

Zoomed out, where this ADR's cost lives (2560x1440, zoom 2.5 -- the camera's maximum, as upstream's; the runs asked for 3 and the probe logged the clamp). Mean frame time
and world draw are in ms:

| Scene | mean, plain | mean, batched | world draw, plain | world draw, batched | draw calls | draw commands, plain | draw commands, batched | alloc KB/frame, batched |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| open field | 19.1 | 21.3 | 4.2 | 5.5 | 1,746 | 7,556 | 1,766 | 929 |
| Britain bank | 53.8 | 72.6 | 19.6 | 30.0 | 8,026 | 27,602 | 8,099 | 3,116 |
| dense forest | 33.4 | 39.0 | 10.9 | 15.3 | 3,422 | 16,852 | 3,418 | 2,162 |
| dungeon | 28.4 | 34.0 | 7.4 | 11.0 | 6,264 | 9,708 | 6,305 | 686 |

At 1280x720 and zoom 0.7 (the default), every scene costs 1.0-1.5 ms plain.
The batched path is 0.3-0.7 ms slower there too.

**What it shows:**

1. **Draw calls did not move at all** (8,026 vs 8,023 at the bank). Godot's
   canvas renderer already merges consecutive rect commands on one texture
   into one draw call. So the 17,000 draw calls of 2026-09-26 are not one per
   sprite; they come from whatever breaks Godot's own batches. Here there
   are 1,613 batcher items for 8,026 draw calls. What breaks them is the
   next question.
2. **Texture changes are rare.** The batcher sees about 43 texture switches a
   frame at the bank, and 436 in the forest. The proposal's premise, that
   the sort interleaves atlas pages and every page change breaks a run, does
   not hold at these scenes. Step 1 (atlas pages as a `Texture2DArray`)
   would remove almost nothing.
3. **Fewer engine calls did not make it faster.** Draw commands fell from
   27,602 to 8,099, but world draw rose from 19.6 to 30.0 ms: building and
   marshalling the arrays costs more than the per-sprite calls saved. The
   prototype allocates 3 MB a frame. A pooled buffer would take the
   allocation away, but not the copy into packed arrays that each call
   makes.
4. **Where the frame goes, zoomed out at the bank (53.8 ms):**
   - world prepare (`FillGameObjectList`, cover, sort): 20.5 ms;
   - world draw: 19.6 ms;
   - the rest, about 14 ms, outside the client's counters (canvas processing
     in the engine);
   - GPU: 0.3 ms.

**Parity:**

- Method: `--perf-parity` takes three frames in a row (plain, batched,
  plain). Every pixel the two plain frames agree on must match in the
  batched frame.
- Result: 0 differing pixels at the login screen and in most scenes, and
  3-142 pixels elsewhere (out of 0.9-3.7 million stable pixels). All the
  differences are isolated specks or a thin object at the screen edge that
  appears in the batched frame alone, consistent with movement between the
  frames. They were not proven to be motion, and did not need to be: the
  path is slower either way.

**Recommendation:**

- Reject batching the sorted world into meshes or triangle arrays. The
  measured costs are world prepare and per-sprite managed drawing, not draw
  calls or texture changes.
- The next targets, in order:
  1. world prepare, against upstream's own prepare as the yardstick;
  2. what breaks Godot's canvas batches (8,026 draw calls from 1,613 items);
  3. the ~14 ms of engine canvas processing, which RenderDump and a Godot
     profiler capture can attribute.
- The prototype stays behind its flag, off by default, so the numbers can be
  reproduced. Remove it if the owner rejects this ADR.

## Date

2026-09-26

## Decision Makers

Project owner; `uo-render-engineer`.

## Summary

Fully zoomed out, GUO draws the same picture as ClassicUO at 11-12 FPS against
its 37-39. The overnight fixes (covering-land mesh cache, texture metric memo,
shadows as transformed rects) took the Minoc mines from 8 to 12 FPS at zoom 2.5
and to 59-60 FPS up to zoom 0.9. What is left does not yield to more of the
same: it is one native call per sprite plus one Godot draw call per texture
change, for ~33,000 sorted objects a frame.

This proposes drawing the sorted world the way upstream does: append quads to
a managed vertex array, and hand Godot one mesh per run, where a run ends only
when the material changes. It needs the atlas pages to become one
`Texture2DArray`, so a texture change stops being a run break.

## Context -- measured, not assumed

Minoc mines, zoom 2.5, 3840x2054, `--zoom-probe` (240-frame averages):

| | ms a frame |
|---|---:|
| wall clock | 85 |
| world prepare (`FillGameObjectList`, `CoverFromBelow`, sort) | 32 |
| world draw (per-object `Draw` -> batcher) | 29 |
| rest of RENDER_FRAME (UI, compositing) | 2 |
| outside the client's counters: Godot turning canvas commands into GPU work | ~20 |
| GPU | ~1 |

~16,900 draw calls a frame, 47,000 canvas objects, GC nil.

- Every sprite is at least one `godotsharp_method_bind_ptrcall`
  (`canvas_item_add_texture_rect_region`); native calls are ~57% of the main
  thread in a trace.
- Draw calls come from texture changes, not canvas items: moving covering land
  into the sprites' item (fewer items) left draw calls at 16,900 and time
  unchanged.
- Texture changes are frequent because the sort interleaves sprites from many
  atlas pages. GUO's pages are 2048 square (`TextureAtlas.MaxPageSize`, chosen
  for upload cost) where upstream's are 4096, so GUO has about four times the
  pages to switch between.
- ClassicUO pays neither cost: `SpriteBatch` appends vertices to an array and
  issues one `DrawIndexedPrimitives` per texture run.

## Proposal

1. **Atlas pages as layers of one `Texture2DArray`.** `TextureAtlas` keeps its
   packing; a page becomes a layer, updated with `texture_2d_update(rid,
   image, layer)`. The sprite's layer index rides in the vertex data (a spare
   channel -- `CUSTOM0.g`, or the unused high bits of R in the packed hue,
   which reach 0x3F at most). `uo_hue_core` samples `sampler2DArray`. Textures
   that are not atlas pages (render targets, gump textures made at runtime,
   the background) keep the rect path.
2. **The batcher appends, not calls.** Inside the world pass, `AddSprite` and
   `AddQuad` write four vertices into a managed buffer, as upstream's
   `SpriteBatch` does. The buffer flushes into one `ArrayMesh` surface when
   the material, the clip or the target changes, or when the frame ends. A
   dynamic mesh per flush slot, created once and updated with
   `mesh_surface_update_vertex_region`, keeps allocation off the frame.
3. **Covering land and shadows fall into the same stream.** Covering land is
   already a mesh quad; its land light moves into the vertex format the world
   stream uses, so `uo_hue_mesh` and `uo_hue` become one shader.

Expected: draw calls from ~17,000 to tens, native calls from ~40,000 to
hundreds. Prepare (32 ms) is untouched by this and is the next target after
it; upstream's own prepare is the yardstick.

## Alternatives

- **More per-call trimming.** Done overnight; the remaining calls are one per
  sprite, and there is nothing left to trim inside them.
- **4096 pages, as upstream.** Halves-to-quarters the texture switches but not
  the per-sprite native call, and the measurements in `TextureAtlas.cs` put a
  4096 page upload at 11 ms. Worth measuring as a cheap first step on its own.
- **A depth buffer instead of the sort** (ADR-0004 amendment note). Would let
  the world be batched by texture outright, but the 2D canvas has none, and
  every sorting fix of the last week (F1, T1, W1) exists because of that.

## Risks

- `Texture2DArray` layers must all be one size; pages that grow in place
  would have to be allocated at their final size.
- The hue shader's precision assumptions (ADR-0002: two channels for a 12-bit
  hue index) must survive the vertex format change; `render_probe` and
  `batcher_probe` re-measure them.
- Anything that reads the canvas item structure (RenderDump, the probes)
  changes with it.

## Validation, if accepted

- `regression_probe -- rendering` and the `ab_compare` sweeps show no pixel
  change beyond moving things.
- `--zoom-probe` at minoc-mines and moonglow: wall clock at zoom 2.5 at or
  above ClassicUO's 37 FPS.

## Amendment 2026-10-19: a depth buffer for the world pass, statics kept on the GPU

**Status:** Accepted 2026-10-22 (owner). Moshu approved this amendment and its
ADR-0001 amendment in the exec-fable terminal at 06:40Z ("I approve").
Written by the perf lane (PERF-GPU)
from the numbers measured 2026-10-18/19. The measurement above still stands:
rebuilding the sorted world into new arrays every frame is slower than
per-sprite calls, and nothing here does that.

### Why now

The probe now splits the world frame into stages (PERF-03,
`docs/perf/2026-10-18_britain_breakdown.md`). This is the "before", measured
on main with the Windows draw defaults of PERF-REG (the land array and merged
cover, `docs/perf/2026-10-19_cover_regression.md`), measured 2026-10-19 with
both changes merged locally. The settings are
2560x1440 at zoom 2.5, an optimised build, and an RTX 4090 with a Ryzen 7 7800X3D.

| Scene | mean ms | FPS | render frame ms | gather ms | sort ms | statics ms | covering land ms | statics drawn | covering land drawn | draw calls | GPU ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| open-field | 6.9 | 145 | 5.7 | 2.18 | 0.35 | 2.09 | 0.12 | 3,381 | 952 | 743 | 0.21 |
| britain-bank | 25.3 | 39.5 | 20.1 | 6.73 | 2.15 | 8.73 | 1.01 | 18,329 | 6,847 | 2,760 | 0.42 |
| dense-forest | 12.5 | 80.0 | 10.3 | 4.00 | 1.18 | 3.84 | 0.26 | 11,449 | 2,048 | 1,347 | 0.24 |
| dungeon | 12.2 | 82.2 | 9.6 | 3.30 | 0.65 | 3.26 | 0.79 | 2,282 | 6,110 | 1,894 | 0.44 |
| cave-roof | 4.4 | 229.3 | 3.7 | 2.73 | 0.06 | 0.20 | 0.00 | 1,036 | 6 | 93 | 0.27 |
| open-sea | 6.4 | 156.1 | 4.9 | 2.04 | 0.25 | 1.73 | 0.13 | 2,638 | 1,070 | 924 | 0.54 |
| walk | 13.0 | 76.7 | 10.8 | 3.27 | 0.29 | 1.96 | 0.16 | 2,737 | 1,046 | 664 | 0.32 |

"Statics ms" includes flushing the covering runs that merged cover holds back:
the draw after a run flushes it, and that draw is usually a static. At the
bank, the statics are 18,329 of the roughly 25,900 objects sorted each frame.
Their walk, their sort share and their own draw take about 14 of the
20 ms render frame.

At zoom 1.0 the bank runs above 200 FPS. A community report gives 25-35 FPS
for a dense city on a 7900 XTX, which matches our zoom 2.5. ClassicUO on the
same machine gives 180+. The GPU is idle, at under 1 ms a frame. The frame is
the CPU walking, sorting and submitting objects, and most of those objects are
statics, which never move:

- **Every static is a sprite, every frame.** `ChunkMesh.IsStaticExcludedFromMesh`
  returns true for every static. So ADR-0004's cached-static path
  (2026-09-26 amendment) has never run.
- **Each frame, each static goes through three steps.**
  - A tile-walk visit: cull, `ProcessAlpha`, the height cap, picking.
  - A place in the sort by `CalculateDepthZ()`.
  - One `canvas_item_add_texture_rect_region` call.
- **The canvas has no depth test, so the sort is the depth buffer.**
  Covering land (ADR-0004, 2026-09-25) exists only because of that. It is
  the second largest cost, and the cause of the 2026-09-27 → 10-18 regression.
- **Upstream** keeps each chunk's statics in a mesh built once. It orders
  them against everything else with a depth buffer: statics at
  `CalculateDepthZ() + 0.5`, `LessEqual`. Meshing statics is upstream's own
  design; GUO's per-sprite path is the deviation.

### Ordering instances against the batcher's sprites

A canvas shader can neither write nor test depth, so a depth key in an
instanced static orders nothing on the canvas by itself. Three ways were
weighed:

1. **Statics in one pass, everything else after.** Wrong: a mobile behind a
   wall would be painted over the wall. Rejected.
2. **Statics in a depth-tested 3D pass, with the sprites testing their own
   key against its depth in the canvas shader.** A canvas rect command
   carries only its colour, which the hue already uses. Giving every sprite
   custom data is the per-frame array path measured above. Rejected.
3. **One world depth buffer (proposed).** The world pass draws into a 3D
   `SubViewport` under an orthogonal `Camera3D`, as upstream draws into its
   depth buffer. Every quad sits at `z = depth key`, with cut-out art
   alpha-scissored and depth-written. Statics, mobiles, items, effects and
   covering land then order on the GPU, whatever order they are submitted in.

```
land (canvas, unchanged) ───────┐
                                ├─ world render target ─ lights (unchanged) ─ UI
world depth viewport (3D) ──────┘
  ├─ static chunk instances : MultiMesh per (chunk, atlas page), rebuilt when the chunk changes
  ├─ dynamic sprites        : MultiMesh per atlas page, written each frame in sorted order
  └─ blended sprites        : after, back to front, depth-tested, not depth-written
```

- **Static chunk instances.** When a chunk's statics change, `ChunkMesh`
  writes one instance for each static that can be instanced. A static
  qualifies under upstream's `ChunkMesh` rule (not internal, animated,
  foliage, tree or rock), and must also not be translucent. Each instance
  carries:
  - the screen rect
  - the UV rect
  - the packed hue and mode
  - the depth key
  - Z, the item height, the can-be-transparent bit and the alpha

  The buffer goes up with `multimesh_set_buffer` once per change.
- **Dynamic sprites.** The batcher keeps its sorted list, now holding only
  what is not instanced: hundreds of objects, not tens of thousands. Each
  becomes an instance in a per-page buffer, uploaded once per page per frame.
- **Blended sprites** (the transparent list and ADR-0003's blend modes) are
  drawn as upstream's `LessEqual` plus draw order gives them.

**Depth key.** `D = 100 * (x + y - first visible diagonal) + 127 + PriorityZ`.
A mobile body adds 50 (its +0.5). A tie field follows: the object's index in
its tile (4 bits) and, for statics, its rank along the diagonal (9 bits). That
rank is the insertion order the sort falls back to today. The visible span at
zoom 2.5 is under 300 diagonals, so the key needs 28 bits. The key has to map
exactly onto a 32-bit float depth buffer, which is why precision is the first
thing measured. If it fails, the fallback is ties by submission inside one page
buffer.

**What a static does per frame today, and where it goes:**

| Today (CPU, per static, per frame) | With instances |
|---|---|
| `Z <= maxGroundZ`, the `_maxZ` roof cut, the height cap | uniforms; the vertex shader collapses the quad. Hidden statics cost the CPU nothing (the cave case). |
| roof and translucency fades (`ProcessAlpha`) | a per-chunk list of statics that are fading, written into their instances; empty when no roof is overhead |
| circle of transparency, shader mode | in the shader, from the instance's Z, height and bit, plus a `player_z` uniform |
| circle of transparency, gradient mode | not instanced: falls back to the sprite path |
| hidden behind a tree, foliage fade, shadows | foliage, trees and rocks are not instanced (upstream's rule) |
| highlight under the mouse, out-of-range and dead hues | one instance rewritten and restored; the dead hue is a uniform |
| lights from statics | each chunk keeps its light statics in walk order, so the `MAX_LIGHTS` cap keeps the same lights |
| picking (highest depth wins) | a query over the chunks under the mouse, pixel-checked as today |
| post-processing id pass (ADR-0023) | the id rides in the instance; the id pass draws the same instances |

### ADR-0001

ADR-0001 governs the renderer. It keeps one presenter, and makes
`CalculateDepthZ()` the only source of depth, for drawing and picking alike.
This amendment keeps both, but it changes two things ADR-0001 states, so it
also needs an **ADR-0001 amendment**, accepted by the owner with this one
on 2026-10-22; see ADR-0001's amendment below its original decision.

**What stays:**

- **One presenter.** The depth viewport is a different way for the one world
  pass to put its objects on screen. It is not a second presenter. There is
  no presenter registry and no alternate look. `--gpu-world` is a fallback
  and parity switch inside that pass, like `--merged-land`, and is removed
  once the owner sets the default. (The `IScenePresenter` of ADR-0001 was
  never written; today the presenter is `RenderLists.DrawWorld` plus
  `UltimaBatcher2D`.)
- **One depth function.** The GPU depth key is computed from
  `CalculateDepthZ()`, never alongside it:
  `D = round(100 * (CalculateDepthZ() - d0))`, where `d0` is the first visible
  diagonal. Because `CalculateDepthZ() = (x + y) + (127 + PriorityZ) * 0.01`,
  this is a lossless integer rescale of that value. A mobile body's `+ 0.5`
  is the same `+ 0.5` the sort adds today (`+ 50` in hundredths). The ordering
  that results is the one the sort gives, applied by the GPU.
- **Picking does not change.** It still takes the highest
  `CalculateDepthZ()` under the mouse, now over the queued objects plus a
  query of the instanced statics in the chunks under the mouse. It never
  reads the GPU depth buffer.

**What changes, needing the ADR-0001 amendment:**

1. **Instanced statics leave the per-frame queue.** ADR-0001 says the
   presenter draws what the queue holds. Instanced statics would instead be
   drawn from buffers that `ChunkMesh` builds when a chunk changes. Land
   already works this way (ADR-0004's chunk land meshes). This extends the
   same exception to statics.
2. **Between instanced statics and queued objects, order comes from the
   depth test, not from queue order.** ADR-0001 says "the order IS the draw
   order". Here the depth key decides, and so it must reproduce the sort's
   tie-break as well: equal `CalculateDepthZ()`, then insertion order
   (`_queued`). Queued objects keep their exact `_queued` order inside their
   page buffer. Instanced statics carry a tie field (their index in the tile
   and their rank along the diagonal) that must equal the insertion order the
   tile walk gives them. Step 1's all-ties scene checks this. Any class of
   static where it does not hold stays queued. This is how upstream orders
   its world (a depth buffer over `CalculateDepthZ()`), so it moves GUO
   towards upstream, not away from it.

### The switch and the fallback

`--gpu-world[=off]`, off by default. The sorted canvas path stays whole and
can be selected at runtime. It is the fallback for gradient transparency, for
any renderer where the precision check fails, and for the parity A/B.
`PerfProbe.ParityToggle` points at the switch, so `--perf-parity` compares the
two paths frame by frame. Android and the web run the Compatibility renderer,
so the switch stays off there until it is measured on a device.

If the depth viewport fails precision or Compatibility, the fallback design
keeps the sorted canvas. All statics go into one `Texture2DArray`, and the
statics that sort between two dynamic sprites are drawn as one multimesh
segment. The order is identical by construction, but every static is still
gathered and sorted every frame, so it removes only the submit cost.

### Order of work, one PR each, the switch off throughout

1. **The viewport and camera, with a pixel test.** One quad, drawn the same
   as the canvas draws it, at zooms 0.5 to 2.5 (texel snapping), plus a
   scene where everything ties (depth precision).
2. **Dynamic sprites only.** Statics stay sprites. Parity proves the depth path.
3. **Static chunk instances.** A before/after probe table scene by scene, and
   parity.
4. **The default.** The owner decides it. The dormant cached-static path and
   covering land are then removed.

### Validation

- `perf_probe --perf-parity --args "--gpu-world"` scene by scene: 0 strict
  pixels, and the frame time before and after. No FPS claim without that
  table.
- `render_diff` max_drawn_diff 0 against the sorted path, `ab_compare`
  over the eight places against ClassicUO, and the visual_ab river scenes.
- The rendering regression probe, picking, the circle of transparency on
  and off, and the post-processing id pass.

### Risks

| Risk | Mitigation |
|---|---|
| depth precision of the 28-bit key | measured first, in the all-ties scene; fallback: submission order, or sorted segments |
| the Compatibility renderer (Android, web) | the switch stays off there until measured on the Thor and the web build |
| canvas and 3D rasterise fractional zoom differently | step 1's pixel test at 0.5-2.5 |
| a per-frame look this list missed | parity at the eight places; a miss sends that class of static back to the sprite path |
| a chunk change costs a buffer rebuild | measured on a walk that crosses chunks (`walk` scene) |
