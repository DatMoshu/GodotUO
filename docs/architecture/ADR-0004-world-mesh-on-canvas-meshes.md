# ADR-0004: The World Mesh Becomes Canvas Meshes, and Land Keeps Its Lighting

## Status

Accepted (amended 2026-09-25)

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

## Amendment 2026-09-25: land drawn again over what is sunk under it

### Problem

`docs/parity_2026-09-23.md` F1. ClassicUO draws the world into a depth buffer
(ortho near `short.MinValue`, far `short.MaxValue`, `LessEqual`; a larger depth
is nearer). Land, statics and items write `CalculateDepthZ() + 0.5`, mobiles
and effects `+ 1`, multis `- 0.01`. GUO has no depth test: it paints the land
bake first and then one CPU-sorted list of everything else. That sort reproduces
the depth buffer everywhere except where an object sits *below* the land in
front of it -- a cellar floor, a sunk foundation (britain-street: 0x001B/0x001C
and floor 0x04B5-0x04B8 at z -20 under a z 0 street). Upstream's land wins those
pixels by depth; GUO painted the land first and the foundation over it.

### Decision

Option (b) of the parity doc. The bake stays as it is. When a static, multi or
item is queued below its own tile's land (`obj.Z < land.Z`),
`RenderLists.CoverFromBelow` also queues the land tiles in front of it -- the
tiles its sprite can reach on screen, at most 8 -- into the sorted list, at the
land's own depth, once per frame each. Sorted, the land lands after the sunk
object and paints over it, which is the result the depth buffer gives.

A covering tile the chunk mesh holds is drawn from its own baked quad
(`MeshLayer.FillSpriteMesh`, `UltimaBatcher2D.DrawMeshSprite`), so it matches
the bake pixel for pixel, stretched or flat, lit or not. The world offset goes
on the canvas item, as `DrawMeshLayer` does; passed as the mesh command's own
transform instead, the draw painted nothing. A flat tile outside the mesh is
drawn the ordinary way; a stretched one outside the mesh cannot be drawn by the
batcher and is left as it was.

Mobiles and effects never trigger it: upstream draws them at `+ 1`, in front of
the land of their own tile, so a mobile below the ground would still be hidden
by land further in front but not by its own tile. GUO's sort also ignored that
`+ 1`, a separate, smaller parity gap: a swimmer was cut off at the chest by the
water statics in front of it. Closed 2026-09-28: a mobile is queued twice, its
shadow and aura at its depth (where upstream draws them) and its body at depth
+ 0.5 (`Mobile.DrawPass`).

The trigger itself takes the highest drawn corner of the ground and of the tile
in front of it (2026-09-28), not the ground's own z: a river bank's stretched
grass rises from the water's z over the water statics standing on it, which the
own-z test never saw, and the water painted over the bank in diamond teeth.

### Rejected

* (a) all land through the sort: correct, but gives up the mesh for every tile
  to fix the few near a cellar.
* (c) drop statics whose top is below the ground in front: cheaper, but a
  heuristic that would hide things the depth buffer only partly hides.
* A real depth buffer: a canvas item has none. It needs the world drawn as 3D
  geometry in its own viewport -- see the note below.

### Validation

* `launchers\dev\side_by_side.bat --place britain-street` with
  `launchers\dev\render_diff.bat`: same objects, same order of sunk pieces and
  covering land (land 3201.25 after foundation 3200.07); the plinth is gone and
  the building meets the street as in ClassicUO.
* `launchers\dev\ab_compare.bat` over the eight places of the parity sweep.
* `launchers\dev\smoke.bat`.
* Re-checked 2026-09-26 on work/render (b59c888, after the statics joined the
  sort, below): the britain-street A/B still matches ClassicUO, and GUO shows
  no plinth or land-over-foundation fault at britain-street-1609, the
  Britain bank district, the Trinsic walls, a Moonglow house, the Vesper
  bridges or the Britain bank home spot. The ClassicUO half of that retake did
  not come up, so those five places are checked by eye in GUO only.

### Note: a depth buffer instead of the sort

The general fix is to stop emulating the depth buffer and have one: draw the
world as quads in a 3D `SubViewport` under an orthographic camera, each at
`z = depth`, alpha-scissored because UO art is cut out. That is what upstream
does, it removes every sort special case (this one and the mobile `+ 1`), and it
lets land stay baked. It replaces the canvas batcher for the world pass, so it is
an ADR of its own, not an amendment.

## Amendment 2026-09-26: cached statics join the world sort

The separate static mesh pass painted every roof before every excluded tree,
regardless of their depths. It also grouped buildings by chunk and texture,
which cannot define painter's order on a canvas without a depth buffer.

Visible cached statics now join `RenderLists` alongside trees, mobiles and
items, using the same `CalculateDepthZ()` key. The scene still updates cached
quad visibility, alpha and hue, and the renderer submits those exact cached
positions and UVs as axis-aligned canvas rectangles at the sorted position.
Static quads have uniform color and do not need stretched land's per-vertex
lighting. This avoids allocating or rebuilding an ArrayMesh per static sprite.
Terrain keeps its bulk mesh path; covering terrain remains in the sorted pass.

The tradeoff is one canvas command per visible static, replacing the old
texture-grouped mesh commands. A real world depth buffer remains the larger
architectural alternative. The existing separate transparent pass is unchanged.

Validation: `dev/regression_probe.tscn -- rendering` reproduces the original
tree-over-roof result, then checks actual GPU pixels for trees behind/in front
of a cached roof, transparent roof pixels, and world offsets. See
`godot/GUO/dev/README.md` for the command.

## Amendment 2026-09-26: the normal travels, the light is per pixel

Supersedes decisions 2 and 4 above. Review pass 2
(`docs/review_2026-09-23_pass2.md`, findings 2, 4 and 5) confirmed three ways
the per-vertex light differed from upstream:

* Upstream interpolates the *normal* across the triangle and normalises and
  lights it per pixel. Lighting the corners and interpolating the light agrees
  at the corners and differs inside the tile, because `normalize` and `max`
  are not linear (the review's ridge example).
* `RGBA8_UNORM` stored the flat-tile value 0.85355339 as 218/255 =
  0.85490196. That is the value `Brightlight` pivots on, so at the default
  brightlight of 1.5 a flat-lit stretched tile came out at 0.85557 where
  upstream's stays at 0.85355339, and every other value carried up to
  ±0.002 of quantisation, multiplied by brightlight.
* The sprite shaders sent a light of 1.0 where upstream's default sprite
  normal gives 0.85355339 (rarely reached: LAND on a sprite).

### Decision

`MeshQuad` carries upstream's four vertex normals (`Normal0`..`Normal3`), and
`CUSTOM0` becomes `RGBA_FLOAT` with the normal in XYZ. The shader include's
varying is `vec3 land_normal`, and `get_light` is upstream's function: normalise
the light direction and the normal, `max(dot, 0) / 2 + 0.5`, then the
brightlight blend. The sprite shaders write the batcher's flat normal
`(0, 0, 1)`, which is what upstream's `SetDefaultNormals` gives every sprite.
`ChunkMesh` now writes the normals exactly as upstream
does, so its deviation marker is gone.

Cost: twelve more bytes per vertex in the rebuilt run meshes (16 against 4).
That matters only when a run is rebuilt, and finding 3 of the same review is
about how often that happens.

`MeshLayer.Dispose` disposes its meshes' C# wrappers since 2026-09-26, so the
consequence above that it "has nothing to free" no longer holds.

### Validation

`launchers\dev\batcher_probe.bat` draws one stretched quad with four
different normals and checks every one of its 8×8 pixels, not just the
corners, against get_light of the normal interpolated across triangles 0 1 2
and 1 3 2, at brightlight 0 and 1.
