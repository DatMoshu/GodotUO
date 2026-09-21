# ADR-0004: The World Mesh Becomes Canvas Meshes, and Land Keeps Its Lighting

## Status

Accepted

## Date

2026-09-20

## Last Verified

2026-09-20 — measured against upstream at pin
`007ef8c3e13dec13fcc02387fe4817ef6f371c85`.

## Decision Makers

Project owner; `uo-render-engineer`.

## Summary

ClassicUO keeps each map chunk's land and statics in a `MeshLayer`: one
persistent GPU vertex buffer, rebuilt only when the chunk changes, with a
per-frame index buffer selecting the visible subset. ADR-0002 deferred this,
because a Godot canvas has no vertex buffer a caller owns.

The port keeps the whole structure and replaces only the buffers. `MeshLayer`
keeps its CPU vertex array, its texture-sorted order, its visibility flags and
its texture runs; each run becomes an `ArrayMesh` drawn with
`canvas_item_add_mesh`, rebuilt when that layer is dirty rather than every
frame. Stretched land keeps its per-vertex lighting, carried in
`ARRAY_CUSTOM0`.

## Context — measured, not assumed

### What MeshLayer is actually for

It is not the drawing path — `RenderLists` still sorts, and the per-object
`Draw` path still exists for everything excluded from a chunk (animated water,
transparent objects, mobiles, effects). `MeshLayer` is the bulk path for the
two things that dominate a frame and almost never change: land tiles and
static tiles. Its whole value is that the geometry is written once per chunk
change and only the *selection* changes per frame.

A Godot canvas item is persistent in exactly the same way: the commands
recorded into it survive until it is cleared. So the property that makes the
upstream design worth keeping is present; only the API differs.

### The thing that nearly did not survive

`IsometricWorld.fx` lights stretched land per vertex:

```
color.rgb *= get_light(IN.Normal);
```

`get_light` is `(max(dot(normal, normalize(0,1,1)), 0) / 2) + 0.5`, blended
toward the flat-tile value `0.85355339` by the `Brightlight` uniform. The four
corners of a stretched tile carry four different normals — that is the whole
point of stretched land, and it is what makes terrain read as terrain rather
than as flat diamonds.

A canvas quad has no normal, and ADR-0002's packed colour is full: R+G hold
the 12-bit hue index, B the mode byte, A the alpha.
`canvas_item_add_triangle_array` offers nothing else. Until this ADR the
shader rendered LAND and LAND_COLOR magenta on purpose, so the gap would show
up in a screenshot instead of looking plausible.

**Measured** — `launchers\dev\mesh_probe.bat`: a `canvas_item` shader *can*
read `ARRAY_CUSTOM0` from an `ArrayMesh` drawn with `canvas_item_add_mesh`,
at 8-bit precision, alongside the ordinary vertex colour in the same draw.
The probe ramps the value across the quad and reads both ends, so a shader
that dropped it or clamped it to a constant fails rather than looking right at
one corner. This was measured because Godot 4.7.2 is past the assistant's
knowledge cutoff and `CUSTOM0` is documented for 3D meshes, not for the 2D
renderer.

## Decision

### 1. Geometry moves to `ArrayMesh`, one per texture run

`BuildVisibleIndices` keeps its name, its texture-run output and its
skip-if-unchanged check. What it builds changes: instead of a `short[]` index
list for a shared `DynamicIndexBuffer`, it builds one `ArrayMesh` per run,
because `canvas_item_add_mesh` binds a single texture per call.

Runs are few — sprites are bucket-inserted in texture-sorted order upstream
precisely so they are, and the textures are atlas pages.

`UploadVertexBuffer`, `UploadVisibleIndices` and `FlushAlphaChanges` are gone.
All three exist to move CPU data into a GPU buffer at the right moment; the
mesh rebuild is that moment now. `FlushAlphaChanges`'s comment about full
`Discard` avoiding partial-update driver bugs on Intel GPUs does not carry
over — there is no partial update to avoid.

### 2. Land lighting is computed on the CPU and carried in `CUSTOM0`

`get_light` is a pure function of the vertex normal and one uniform, so the
per-vertex half is computed where the normal already lives — at mesh build
time — and one byte per vertex reaches the shader. The uniform half stays in
the shader, because `Brightlight` follows a profile setting the player can
change without the chunk becoming dirty.

The stored value is upstream's `base`, before the `Brightlight` blend, and it
is bounded in `[0.5, 1]`, so `RGBA8_UNORM` resolves it to better than one step
of the 8-bit output.

### 3. The mode byte stays where it is

The obvious alternative was a land-only material, which would free the mode
byte for the light value and need no mesh. Rejected: a `MeshLayer`'s land
holds *both* stretched tiles (LAND, LAND_COLOR) and flat ones (NONE, HUED) —
`ChunkMesh.TryAddLand` picks between them per tile — so the layer would have
to be split in two and drawn twice, reordering it against the statics layer.

### 4. One shader include still, with the light as a varying

`uo_hue_core.gdshaderinc` gains `varying float land_light`, and its LAND and
LAND_COLOR branches do what upstream does instead of rendering magenta. The
two non-mesh shaders assign it `1.0`; the mesh shader assigns it from
`CUSTOM0.r`. As with `packed_hue`, the assignment is duplicated because Godot
will not let a varying be written from a shared helper.

## Alternatives Rejected

**Draw the mesh through the ordinary batcher every frame.** Simplest, and the
client managed without `ChunkMesh` before upstream added it. Rejected: it
throws away the one optimisation the whole class exists for, and it would have
to be undone to reach parity later. Project rule 3.

**Quantise the light into the spare four bits of the hue index.** 12 of the 16
R+G bits are used, so four remain: 16 levels across `[0.5, 1]`. Rejected once
the probe passed — 16 levels band visibly on a smooth slope, and this is
exactly the failure mode that is hard to attribute later.

**One canvas item per chunk, holding all its runs.** Attractive, but a canvas
item is also the unit of clipping, blending and draw order in ADR-0002's
batcher, and the chunk layers are drawn interleaved with the per-object lists
under a world offset. Keeping the mesh inside the batcher's item stream is
what keeps that ordering correct.

## Consequences

* Terrain shading works, rather than being a documented absence. The magenta
  LAND placeholder in `uo_hue_core.gdshaderinc` is gone.
* A chunk's geometry is rebuilt when its visibility, alpha or hue changes,
  not every frame — the same condition upstream re-uploads under.
* `MeshLayer` no longer holds a GPU resource, so `Dispose` has nothing to
  free. It is kept, because every caller calls it and it will matter again if
  the meshes are ever pooled.
* `ChunkMesh.Build` loses its `GraphicsDevice` parameter, the last one in the
  tree.

## Validation

* `launchers\dev\mesh_probe.bat` — `CUSTOM0` reaches a `canvas_item` shader
  from `canvas_item_add_mesh`, at both ends of a ramp, with the ordinary
  vertex colour intact in the same draw.
* `launchers\dev\batcher_probe.bat` — the hue packing is exact through the
  triangle-array path as well as the fast path, which is what the mesh path's
  per-vertex colour relies on.
