# ADR-0037: Statics on the GPU (amends ADR-0007)

## Status

Proposed. Amends ADR-0007 (the sorted world in batched meshes), which stays
Proposed with its recommendation to reject *per-frame* batching; this ADR
takes up the part ADR-0007 left open, the statics, on numbers measured
2026-10-18 (PERF-03). The owner accepts or rejects.

## Date

2026-10-18

## Last Verified

2026-10-18, against main at 96ce0ff2 plus the PERF-01/PERF-03 probe.

## Decision Makers

Project owner (Moshu); `uo-render-engineer`; written by guo-worker-perf.

## Summary

A UO frame is mostly statics: at the Britain bank zoomed out, about
18,329 of the 25,890 objects GUO sorts and draws each frame are
statics, and every one of them costs the CPU a tile-walk visit, a place in
a sort and one `canvas_item_add_texture_rect_region` call, every frame,
although a static never moves. ClassicUO does not pay this: it keeps each
chunk's statics in a mesh built once and lets a depth buffer order them
against everything else.

This ADR moves the statics that never change their look on their own --
most of them -- into **per-chunk instance buffers, built when a chunk
changes and never per frame**, drawn by the GPU with per-instance position,
UV, hue and a depth key. It answers how those instances still sort against
the mobiles, items and effects the batcher draws every frame, and how
picking, transparency, roof hiding and hues stay identical. Everything sits
behind a toggle, off by default, until the parity checks pass; the owner
then picks the default.

## Engine Compatibility

| | |
|---|---|
| Engine | Godot 4.7.2 stable, mono/.NET, Forward+ and Compatibility (Android, web) |
| APIs | `RenderingServer.multimesh_*` (`multimesh_set_buffer`, custom data, colours), `Texture2DArray` (`texture_2d_layered_create`, `texture_2d_update(rid, image, layer)`), `SubViewport` with a `Camera3D` in orthogonal projection, spatial shaders (`depth_draw_always`, `render_mode unshaded`, alpha scissor), reverse-Z depth (Godot 4.3+) |
| Knowledge risk | MEDIUM: depth precision of the reverse-Z float depth buffer on the Compatibility renderer and its texel snapping at fractional zoom must be measured, not assumed (Validation). |

## ADR Dependencies

- ADR-0001 (render presenter seam): the seam is unchanged; this changes
  how the presenter draws, not what the game layer hands it.
- ADR-0002 (batcher on the canvas): the batcher keeps drawing everything
  but the instanced statics; its world pass gains a second consumer.
- ADR-0004 (world mesh on canvas meshes) and its 2026-09-26 amendment
  ("cached statics join the world sort"): superseded for statics. That
  amendment's cached-static path is dormant today (below).
- ADR-0007 (sorted world in batched meshes): amended. Its measurement
  stands -- marshalling the sorted world into new arrays *every frame* is
  slower than per-sprite calls -- and this ADR does not do that: static
  buffers are written when a chunk changes, not per frame.
- ADR-0023 (post-processing): the object-id pass must see instanced
  statics (Validation).

## Context

### Problem Statement

Community report (a 7900 XTX and a 5800X3D, 2560x1440, zoom 1.0, the
current build; user reports, not our measurements): a dense static city
25-35 fps; vegetation only 125-180; a cave under a land-tile roof 14-17;
open sea with no statics 236-250. ClassicUO on the same machine 180+.
The owner's public target: the dense city from about 30 to well over 100
fps.

Our own numbers, the "before" of this ADR (PERF-03,
`docs/perf/2026-10-18_britain_breakdown.md`, RTX 4090 and Ryzen 7 7800X3D,
2560x1440):

| Scene (2560x1440, zoom 2.5) | mean ms | FPS | gather ms | sort ms | statics draw ms | covering land ms | outside the client's counters ms | statics drawn | covering land drawn | draw calls | GPU ms |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| britain-bank | 39.8 | 25.2 | 7.90 | 2.56 | 5.76 | 4.96 | 15.0 | 18,329 | 6,847 | 11,929 | 0.69 |
| dense-forest | 19.1 | 52.5 | 5.04 | 1.30 | 3.06 | 1.24 | 6.4 | 11,449 | 2,048 | 5,449 | 0.32 |
| dungeon | 23.9 | 41.8 | 4.53 | 0.79 | 1.37 | 3.71 | 11.4 | 2,282 | 6,116 | 9,308 | 0.71 |
| cave-roof | 4.5 | 223.7 | 2.89 | 0.06 | 0.20 | 0.01 | 0.5 | 1,036 | 6 | 92 | 0.26 |
| open-sea | 9.6 | 104.6 | 2.83 | 0.25 | 0.82 | 0.70 | 3.7 | 2,638 | 1,070 | 3,295 | 0.84 |

"Outside the client's counters" is Godot turning the canvas commands into
GPU work; it scales with the command count (31,763 at the bank, 58% of them
statics). At zoom 1.0 the bank runs at 229 FPS; the community's 25-35 FPS
city matches our zoom 2.5.

Two findings shape this ADR. First, statics account for about half of the
zoomed-out frame (their gather and sort share, their own draw and their
share of the engine's command processing: about 20 of 40 ms at the bank).
Second, covering land, which exists only because the canvas has no depth
buffer, is the next largest cost and the main part of a regression since
2026-09-27 (the same commit ran the bank at 24.9 ms tonight; PERF-REG fixes
that first, as its own PR, and the "before" of this ADR is re-measured on
top of it).

The GPU is idle (under 1 ms a frame). The frame is the CPU walking,
sorting and submitting objects, and statics are most of the objects.

### Current State

Measured in code, 2026-10-18 (file references are to `godot/GUO/src`):

- **Every static is a sprite, every frame.** `ChunkMesh.IsStaticExcludedFromMesh`
  returns true for every static (`Game/Map/ChunkMesh.cs:484-508`) and has
  since the file was written, so the "cached statics" path of ADR-0004's
  2026-09-26 amendment (`RenderLists.AddMeshStatic`,
  `UltimaBatcher2D.DrawStaticMeshSprite`, the mesh highlight in
  `GameScene.DrawWorld`) never runs.
- **Gather.** `GameScene.FillGameObjectList` walks every visible chunk and
  tile; `AddTileToRenderList` (`GameSceneDrawingSorting.cs:777`) culls each
  static, runs `ProcessAlpha` (roof and translucency fades), the height cap,
  lights, `CheckIfBehindATree`, the gradient circle of transparency, picking
  (`PushToRenderQueue` -> `CheckMouseSelection`), and queues it.
- **Sort.** `RenderLists.DrawWorld` sorts one list of statics, items,
  multis, effects, mobiles (twice: shadow at the depth, body at depth + 0.5)
  and covering land by `CalculateDepthZ()` = `(x + y) + (127 + PriorityZ) *
  0.01`, ties by insertion order (`RenderLists.cs:302-307`).
- **Submit.** `Static.Draw` -> `UltimaBatcher2D.AddSprite` -> one
  `CanvasItemAddTextureRectRegion` per static (`UltimaBatcher2D.cs:1925`).
  The canvas has no depth buffer; the sort *is* the depth buffer.
- **Upstream** keeps a static mesh per chunk (`ChunkMesh`, excluding only
  internal, animated, foliage, tree and rock statics), draws it without a
  sort, and orders it against the four unsorted lists by a depth buffer:
  ortho projection over the `short` range, statics at `CalculateDepthZ() +
  0.5`, `LessEqual` (`ClassicUO.Renderer/Batcher2D.cs:1256-1264`,
  `Game/Scenes/RenderLists.cs:199-244`). Meshing statics is upstream's own
  design; GUO's per-sprite path is the deviation.

### Constraints

- Pixel parity with the current GUO picture and with ClassicUO: a fallback
  toggle, off by default, until `render_diff` max_drawn_diff is 0 and the
  eight-place pixel diff passes.
- `default_texture_filter=0`: nearest sampling everywhere, including any new
  viewport and texture array.
- Android and the web run the Compatibility renderer: whatever is chosen
  must work there or degrade to today's path by itself.
- Port fidelity (AGENTS.md, L0): the change stays in GUO-owned renderer
  files plus marked seams in `GameScene`/`RenderLists`.

### Requirements

1. A static whose look depends only on itself costs nothing per frame on
   the CPU while its chunk is unchanged.
2. Draw order identical to the sorted path, against every other object.
3. Picking, the circle of transparency, roof and height hiding, the
   translucency and roof fades, hues (including partial hues, highlight and
   the dead/grey hue), lights from statics and the post-processing id
   buffer behave exactly as today.
4. Cave and roof culling (`Z <= maxGroundZ`, `_maxZ`) without visiting the
   hidden statics on the CPU.

## Decision

### The hard part: ordering instances against the batcher's sprites

Godot's canvas draws in command order and has no depth test, and a canvas
shader cannot write or test depth. A depth key in an instanced static's
shader therefore orders nothing on a canvas by itself. Three ways were
weighed:

1. **Split passes by layer** (statics first, then everything else): wrong.
   A mobile behind a wall would be painted over it. Rejected outright.
2. **Composite: statics in a depth-tested 3D pass, sprites tested against
   its depth key in their canvas shader.** A sprite would need its own
   depth key in the shader, and a canvas rect command carries only its
   colour, which the batcher already fills (hue, mode, alpha:
   `uo_hue_core.gdshaderinc:27-28`). Moving every sprite to a command that
   carries custom data is ADR-0007's measured-slower path. Rejected.
3. **One world depth buffer (chosen): the world pass draws into a 3D
   `SubViewport` under an orthogonal `Camera3D`**, as upstream draws into
   its depth buffer: every quad at `z = depth key`, cut-out art
   alpha-scissored and depth-written, so statics, mobiles, items, effects
   and covering land order by depth on the GPU in any submission order.
   This is the "note: a depth buffer instead of the sort" of ADR-0004,
   2026-09-25, scoped to the world pass.

### Architecture

```
land bake (canvas, unchanged) ─┐
                               ├─ world render target ─ lights (unchanged) ─ UI
world depth viewport (3D) ─────┘
  ├─ static chunk instances   : MultiMesh per (chunk, atlas page), built on chunk change
  ├─ dynamic sprites           : MultiMesh per atlas page, written each frame in sorted order
  │                              (mobiles, items, effects, excluded statics, covering land)
  └─ blended sprites           : drawn after, back to front, depth-tested, not depth-written
```

- **Static chunk instances.** When a chunk's statics change (load, add,
  remove, theme, season), `ChunkMesh` writes one instance per *instanceable*
  static into a float buffer per atlas page: screen position and size (a
  `Transform3D` row pair), UV rect, packed hue and mode (as the batcher
  packs vertex colour today), and custom data: the depth key, Z, the item
  height, the `_canBeTransparent` bit and the alpha. `multimesh_set_buffer`
  once per change. Instanceable is upstream's `ChunkMesh` rule (not internal,
  not animated, not foliage, tree or rock) plus not translucent and not
  themed with a size change; everything else stays a sprite.
- **Dynamic sprites.** The batcher's world pass keeps building its sorted
  list, now holding only non-instanced objects (hundreds, not tens of
  thousands). Instead of a canvas rect per sprite it appends an instance to
  a per-page buffer, uploaded once per page per frame. The list is still
  sorted, so ties resolve exactly as today; the sort is over far fewer
  objects.
- **Blended sprites** (transparent list, alpha < 255, additive and other
  blend modes of ADR-0003) draw after the opaque ones, back to front from
  the same sorted list, depth-tested but not depth-written, which is what
  upstream's `LessEqual` with draw order gives them.

### Depth key

`key = CalculateDepthZ()` in hundredths, made relative to the camera's
first visible diagonal so a 32-bit float carries it exactly: `D = 100 *
(x + y - diagonal0) + 127 + PriorityZ`, then `+ 50` for a mobile body (its
+0.5), then a tie field: the object's index in its tile's list (4 bits)
and, for statics, its walk rank along the diagonal (9 bits), which is the
insertion order `ByDepth` falls back to today. Mapped linearly onto the
camera's near/far. The visible span at zoom 2.5 and 2560x1440 is under 300
diagonals, so `D` needs 15 bits and the key 28: inside a 32-bit float
depth buffer's mantissa only if the mapping is exact, which is why
Validation measures precision before anything else and the fallback is to
order ties by submission inside one page buffer.

### Per-frame look, item by item (Requirement 3)

| Today, per static per frame (CPU) | With instances |
|---|---|
| `Z <= maxGroundZ` gate; `_maxZ` roof cut; 150 height cap | uniforms `max_ground_z`, `max_z`; the vertex shader collapses the quad. Hidden statics cost the GPU a vertex, the CPU nothing (the cave case). |
| `ProcessAlpha` roof/translucency fade, 25 a step every 20 ms | per-chunk "fading" list: only statics whose alpha is not 255 or whose Z crosses `_maxZ` are walked, and their alpha is written into the instance; an empty list when no roof is above the player. |
| Circle of transparency, shader mode | `TransparentTest(playerZ + 5)` in the shader from the instance's Z, height and bit and a `player_z` uniform; the circle itself is already a shader discard (`uo_hue_core.gdshaderinc:173-190`). |
| Circle of transparency, gradient mode (CPU alpha per object) | not instanced while gradient mode is on: the toggle falls back to the sprite path. |
| `CheckIfBehindATree`, foliage fade, shadows | foliage, trees and rocks are not instanceable (upstream's rule). |
| Highlight hue under the mouse, out-of-range and dead hues | the highlighted static's instance hue is written and restored the next frame (one instance); the dead/grey hue is a uniform. |
| Lights from statics (`AddLight`) | each chunk keeps its light-emitting statics in walk order; the gather visits only those, so the `MAX_LIGHTS` cap keeps the same lights. |
| Picking (`CheckMouseSelection`, highest depth wins) | a query: the statics of the chunks under the mouse whose screen rect contains it, pixel-checked as today, highest depth kept against the sprites' winner. |
| Post-processing id buffer (ADR-0023) | the id rides in the instance data; the id pass renders the same instances with its own material. |

### Key Interfaces

- `ChunkMesh.StaticInstances` (per page: `Rid multimesh`, `float[] buffer`,
  `int count`, `bool dirty`), built in `ChunkMesh.Build`.
- `WorldDepthPass` (new, `src/Render/`): owns the `SubViewport`, camera,
  page materials and per-page dynamic buffers; `Begin(view)`,
  `AddSprite(...)` (the batcher's world pass forwards here when on), `End()`.
- `UltimaBatcher2D.GpuStatics` (static bool, `--gpu-statics[=off]`), with
  `PerfProbe.ParityToggle` pointed at it for `--perf-parity`.
- `uo_hue_core.gdshaderinc` split so its hue and light functions compile in
  a spatial shader too (`uo_hue_world.gdshader`).

### Implementation Guidelines

1. Phase 1, statics only through the depth viewport, everything else still
   on the canvas *behind* it is not possible (Decision 2), so phase 1 is the
   whole world pass behind the toggle, built in this order: the viewport and
   camera with a pixel-exact quad test; dynamic sprites only (statics still
   sprites) -- parity proves the depth path; then static instances.
2. Atlas pages stay 2048 and never grow (`TextureAtlas.cs:61`), so a page
   is a layer of a `Texture2DArray` or stays its own texture; one material
   per page is enough at a few dozen draw calls.
3. Keep the sorted canvas path intact and selectable at runtime; it is the
   fallback for gradient circle of transparency, for any renderer where the
   depth precision check fails, and for the parity A/B.

## Alternatives Considered

### Alternative 1: Faster per-frame batching on the canvas (ADR-0007)

Measured 2026-09-27: slower, because the arrays are rebuilt every frame.
Not repeated.

### Alternative 2: Instanced statics in sorted segments on the canvas

Keep the CPU sort, put all statics into one `Texture2DArray`, and between
two consecutive dynamic sprites draw the statics that sort between them as
one multimesh segment. Order identical by construction and no 3D viewport,
but every static is still gathered, sorted and copied into a segment
buffer every frame, so it removes the submit cost only. Kept as the
fallback design if the depth viewport fails its precision or Compatibility
checks.

### Alternative 3: Cache the gather and sort, keep per-sprite calls

Removes part of prepare, none of the submit. Too small for the target.

## Consequences

### Positive

- The statics' per-frame CPU cost goes to nearly nothing while their chunk
  is unchanged; the cave under a land roof stops costing the CPU the
  statics it hides.
- The world is ordered the way upstream orders it, which also retires the
  sort special cases (covering land, the mobile +0.5) as the depth key
  carries them.
- Draw calls fall to roughly one per (chunk, page) plus one per page of
  dynamic sprites.

### Negative

- A second world drawing path to keep identical until the old one is
  removed, and a spatial copy of the hue shader.
- Chunk changes now cost a buffer rebuild (a house placed, a season
  change).

### Neutral

- The light pass, the land bake and the UI are unchanged.

## Risks

| Risk | Mitigation |
|---|---|
| Depth precision: ties and the 28-bit key in a float depth buffer | measure first (an all-ties test scene); fallback: ties by submission inside one page buffer, or Alternative 2 |
| Compatibility renderer (Android, web) differs | the toggle stays off there until measured on the Thor and the web build |
| Fractional zoom texel snapping differs between canvas and 3D raster | pixel test of one quad at zooms 0.5-2.5 before anything else |
| A per-frame look this ADR missed | the parity A/B at the eight places plus the regression probe; a miss falls back to the sprite path for that static class |

## Performance Implications

Expected, to be replaced by the measured "after": world prepare loses the
statics' gather share, world draw loses the statics' submit share and all
of covering land, and the engine processes about 60% fewer commands at
the bank
(both in the table under Problem Statement). No claim is made before the probe table exists.

## Migration Plan

1. This ADR (Proposed) -> owner decision.
2. PR: the depth viewport with dynamic sprites only, toggle off, parity.
3. PR: static chunk instances, toggle off, before/after probe at the
   Britain bank and the four community scenes, parity.
4. Owner decides the default. The dormant cached-static path is removed.

## Validation Criteria

- `launchers\dev\perf_probe.bat --perf-parity --args "--gpu-statics"`: pixel
  parity per scene (0 strict pixels), before/after frame time at
  britain-bank, city, forest, cave-roof and open-sea.
- `launchers\dev\render_diff.bat`: max_drawn_diff 0 against the sorted path.
- `launchers\dev\ab_compare.bat` over the eight places against ClassicUO.
- `dev/regression_probe.tscn -- rendering`, the post-processing id pass,
  picking at the eight places, the circle of transparency on and off.
- Android (Thor) and web: the toggle off by default until measured.

## GDD Requirements Addressed

None (engine port; parity and performance).

## Related

- `docs/perf/2026-10-18_britain_breakdown.md` (PERF-03, the "before").
- ADR-0004 note "a depth buffer instead of the sort" (2026-09-25).
- `docs/perf/2026-09-27_batched_vs_plain_2560_zoom2.5.md`.
