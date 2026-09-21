# ADR-0002: UltimaBatcher2D on Godot's Canvas, with Hue Carried in Modulate

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

`UltimaBatcher2D` is the last large blocker in the port: with every staged-out
area enabled it accounts for 78 of the 182 remaining compile errors, and the
UI is written entirely against it. We reimplement it on Godot's canvas item
API, keeping its public signatures byte-for-byte so the ~120 call sites port
unchanged, and we carry ClassicUO's per-sprite hue vector in the per-quad
**modulate colour** rather than inventing a vertex format or a canvas item per
hue.

## Current State — measured, not assumed

`tools/port_triage` and a call-site count over the ported tree give:

| Member | Call sites | Notes |
|---|---:|---|
| `Draw` (7 overloads) | 80 | all funnel into one private `AddSprite` |
| `DrawTiled` | 18 | loops calling `Draw` |
| `DrawRectangle` | 12 | four 1px `Draw` calls |
| `ClipBegin` / `ClipEnd` | 11 / 11 | scissor stack |
| `DrawString` | 3 | needs `Fonts`/`SpriteFont` first |
| `SetWorldOffset` / `Reset` | 2 / 2 | integer translation |
| `SetStencil` | 2 | world renderer only |
| `GraphicsDevice` | 2 | FNA handle, see below |
| `DrawLine` | 2 | one rotated `Draw` |
| `GetDynamicIndexBuffer` | 1 | FNA escape hatch |
| `DrawDirectIndexed` | 1 | FNA escape hatch |

Two facts decided this ADR:

1. **Every `Draw` overload funnels into a single private `AddSprite`**, taking
   source rect, destination rect, a hue `Vector3`, rotation, origin, depth and
   a `SpriteEffects` flip. One choke point to implement, not seven.

2. **The hue is already a three-float per-sprite attribute.** Upstream passes
   `(hue index, shader mode, alpha)` down as a vertex attribute and its shader
   branches on the mode. It is not global state.

## Decision

### 1. Keep the public signatures exactly

Every public member above keeps its name, parameter list and order. The type
is reimplemented; its interface is not redesigned. This is what makes the 120
call sites, and the whole UI, port without edits — and what keeps them
mergeable against live upstream.

Two exceptions, both forced and both narrow:

* `UltimaBatcher2D(GraphicsDevice)` and the `GraphicsDevice` property go.
  Godot has no device handle to thread. Two call sites.
* `Begin(Effect)`, `SetBlendState`, `SetSampler` and `SetStencil` take FNA
  pipeline-state objects. They become GUO enums with the same call shape.

### 2. Carry the hue vector in the per-quad modulate colour

Godot's canvas item API has no per-quad custom vertex attribute, but
`CanvasItemAddTextureRectRegion` takes a **modulate** colour, and that is a
per-quad channel to the shader.

Two things had to be true for this to work, and **both were measured** on
4.7.2 rather than assumed, because the engine is past the assistant's
knowledge cutoff. `launchers\dev\render_probe.bat` reproduces all of it.

**Finding 1 — the modulate must be read in `vertex()`, not `fragment()`.**
By the time `fragment()` runs, `COLOR` has already been multiplied by the
sampled texture. Measured: modulate `(0.25, 0.5, 0.75)` over a mid-grey
texture arrives in `fragment()` as `(0.125, 0.247, 0.373)` — exactly halved.
`vertex()` still sees the raw value, so the shader captures it there and
carries it across in a `varying`. The first draft of this ADR had it wrong.

**Finding 2 — the channels are 8-bit, so the hue index needs two of them.**
Measured: values one 1/255 step apart come back 0.00392 apart, and values
closer than that are identical. A hue index runs to ~3000 and needs 12 bits.
Round-tripping 0…4095 through a single channel gave a worst error of **3** —
silently the wrong hue. Split across two channels the worst error is **0**.

So the encoding is:

```
R  hue index >> 8      B  shader mode, with circle-of-transparency in bit 7
G  hue index & 0xFF    A  alpha, 0..1
```

`ShaderHueTranslator.GetHueVector` signals circle-of-transparency by adding
`1f` to the alpha component, which will not fit a 0..1 channel. The batcher
undoes that as it encodes — `circle = Z > 1`, `alpha = Z > 1 ? Z - 1 : Z` —
and carries the flag in the spare top bit of the mode byte, where modes only
reach about 30.

A single `canvas_item` shader decodes those and reproduces
`ShaderHueTranslator`'s modes against the hue palette uploaded as a texture.
The hue stays per-sprite data flowing to a shader, exactly as upstream has it,
so one canvas item and one material serve every sprite regardless of hue.

### 3. Depth becomes canvas item draw order, not a Z buffer

Upstream writes `layerDepth` into the vertex Z and relies on a depth buffer.
Godot's 2D canvas has no depth test; it paints in submission order. Since
ADR-0001 keeps `RenderLists` — which already sorts by `CalculateDepthZ()`
before anything is drawn — submission order *is* the sorted order, and the
depth argument becomes a sort key rather than a Z write.

This is why ADR-0001 mattered: having kept the sorting seam, we do not need a
depth buffer in 2D at all.

#### Amendment, 2026-09-21: the seam had to be made to sort

The paragraph above was wrong about upstream when it was written, and the
mistake cost a visibly broken world. `RenderLists` did not sort. Upstream
keeps four lists -- land, statics, animations, effects -- and walks them one
after another, which resolves nothing: what paints over what is settled per
pixel by the depth buffer `GameScene.DrawWorld` turns on, over the very
`CalculateDepthZ()` value the lists carry. Submission order upstream is not
depth order and never was. The UI is the same shape: `UIManager.Draw` also
turns the depth buffer on, hands every control an ever-increasing
`layerDepth`, and flushes two gump queues one after the other.

On a canvas with no depth test both splits showed. Furniture inside a house
painted over its roof; a chair queued as a baked chunk static painted over
everything dynamic; journal text painted across a world map opened on top of
it; a status gump landed between a map's picture and its own frame.

The seam now does what this ADR claimed it did:

- `RenderLists` merges statics, multis, items, mobiles, corpses and effects
  into one list and sorts it by `CalculateDepthZ()`, with the queue sequence
  breaking ties so a pile on one tile cannot shimmer (`List.Sort` is not
  stable). This is the depth buffer done on the CPU, and it is exact rather
  than approximate because UO sprites are cut out: a pixel is opaque or it is
  absent, so ordering whole sprites answers as ordering pixels would.
- The two gump queues become one. That needs no sort -- `layerDepth` only
  goes up as the control tree is walked, so arrival order already is depth
  order -- but `ScissorControl`, which queued its clip into both, now queues
  it once.
- `ChunkMesh.IsStaticExcludedFromMesh` excludes everything. A baked chunk is
  one draw for a whole 8x8 of statics and cannot take part in a sort, so
  baking and correct order are exclusive. Land is still baked; it is under
  everything by construction. Measured cost of giving up the statics mesh:
  none at the resolution and object counts the playtest runs at -- 16.66 ms a
  frame before and after, and 3,600 frames of continuous walking held it.

The way to have both is the depth buffer itself: a 3D pass that writes Z, with
the sprites as camera-facing quads. That is a larger decision than this
amendment and is not taken here.

**The class is closed.** `grep -rn SetStencil` over upstream finds eleven
sites, and only two of them are live: `GameScene.DrawWorld` and
`UIManager.Draw`, the world and the UI. Both are now reproduced by sorting.
The rest are commented out upstream -- `RenderLists`' transparent pass,
`CheckerTrans`' checker -- and are commented out identically here, so they
carry no behaviour to lose. Plugin.cs's is a command-buffer id and is not
ported. There is no third place where this port draws in list order while
upstream leans on a depth test, and a check for one belongs in a future
upstream merge rather than in a search of the code as it stands.

### 4. The two FNA escape hatches stay unimplemented, loudly

`GetDynamicIndexBuffer` and `DrawDirectIndexed` exist for the world mesh, which
is `MeshLayer`'s job and a separate decision. They throw
`NotSupportedException` with a message naming this ADR rather than returning
quietly. A renderer that silently draws nothing is the specific failure mode
project rule 6 exists to prevent.

## Alternatives Rejected

**One canvas item per distinct hue.** No shader needed. Rejected: UO uses
thousands of hues, and a canvas item per hue turns one draw call into
thousands. It also breaks draw order, since ordering is per canvas item and
ADR-0001's sort is global.

**A custom mesh with a real vertex format.** Closest to upstream, and gives
four spare floats instead of one. Rejected for now: it means building and
uploading vertex buffers per frame, which is the FNA layer this port exists to
delete, and modulate already carries everything the hue needs. Revisit only if
a shader mode is found that needs a fourth channel.

**Keep FNA's batcher and render to a texture.** Rejected outright — it retains
the dependency the project exists to remove.

**Redesign the batcher API to something more Godot-shaped.** Rejected: it would
touch all ~120 call sites, none of which are FNA-specific, and every one would
have to be reconciled by hand on each upstream merge. Project rule 3: parity
before improvement.

## Consequences

* The UI tree compiles once this lands; `UltimaBatcher2D` is 78 of the
  remaining 182 errors.
* `DrawString` stays blocked on `Fonts`/`SpriteFont`, which is the next piece.
* Anything drawn through the escape hatches will throw rather than render, so
  the world mesh is visibly missing rather than quietly wrong.
* The hue shader is now a single file with a single responsibility, and can be
  compared against upstream's by eye.

## Validation

* `grep -rn "GraphicsDevice" godot/GUO/src/Render/` returns nothing.
* Exactly one private `AddSprite`; every public `Draw` overload reaches it.
* A screenshot of hued art matches the original client for the same hue id.
* Nothing in `src/Render` filters a texture: project rule 7 applies to every
  new material and viewport this introduces.
