# ADR-0007: The Sorted World Drawn in Batched Meshes (proposal)

## Status

Proposed. **Recommendation: Reject**, measured 2026-09-27 (Epic B, B2; see
"Measured" below). It stays Proposed until the owner signs off.

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

Zoomed out, where this ADR's cost lives (2560x1440, zoom 3). Mean frame time
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
