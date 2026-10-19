# ADR-0039: Realtime Lights, a tile-grid light field for the 2D world

Status: Proposed, 2026-10-19 (the owner chose the direction in the lighting
seat's window; the HD lighting draft, PR 57, stays a draft and is not built on).

## Context

Classic UO lighting is a screen-space overlay: the light target is cleared to
one grey (the shard's light level) and `light.mul` sprites are added on top,
then the world is multiplied by that target (`GameScene.PrepareLightsRendering`,
`RenderTargets.Draw`). Light passes through walls, a room next to a torch is
as bright as the one holding it, and the world is lit only when the shard
says it is dark.

The HD lighting draft stacked screen-space passes on that overlay: a sun
march over a height buffer driven by a manual clock, a 24-tap radiance
average, a luma Sobel "normal" and an ACES tonemap. Reviewed in the engine,
the manual sun fought the shard's light level (a noon scene stayed black),
the square occluders cut hard bites out of the light sprites, the radiance
term bled through walls and the Sobel lit pixel-art edges on the wrong side.
Those faults are structural: the art is pre-shaded with a fixed light
direction, and the shard owns the clock.

## Decision

An L1 client option, **Realtime lights**, off by default. It is built into the
client (no pack); it follows ADR-0027's amendment for how an L1 render option
plugs in (profile field, both Options interfaces, off-state untouched) and
ADR-0023's rules (stateless, nearest, the world draw unchanged).

1. **Light is a field on the tile grid, not a sprite on the screen.** Each
   frame the scene collects the same light offers the classic loop collects
   (`AddLight`), extended with the source's tile X, Y, Z. For each light the
   CPU computes a small map of the tiles it reaches (radius from the
   `light.mul` sprite size) by stepping a line through a blocker grid built
   from tiledata: walls, and impassable statics taller than a step, block;
   doors, windows and the light's own tile do not. A blurred 3x3 softens the
   edges. The per-light map is cached and recomputed only when the light or
   the blockers around it change. The sum of ambient plus every light is the
   light field.
2. **The shard owns the clock.** Ambient comes from the light level the shard
   sends (0x4F / 0x4E, through `IsometricLight`), mapped through a colour
   ramp: full daylight at level 0, warm at dusk, blue-grey at 30. The
   existing Options **Light level** override is the override slider. Tiles
   under a roof get a lower daylight ambient, so interiors read as indoors.
3. **The field is drawn into the light target as geometry.** Land tiles are
   drawn as their own iso quads with one light colour per corner (the Diablo
   II method), so the ground shades smoothly without a texture lookup. Tall
   statics are drawn again, masked by their own sprite alpha, in the flat
   colour of their tile, so a wall takes its tile's light rather than the
   ground behind it on screen. Everything draws through one canvas item into
   a GUO-owned target the size of the light target; `RenderTargets.Draw`
   blends it exactly where it blends the classic light target, with the
   classic darkness blend. The world draw itself is not touched.
4. **No sun shadows, no tonemap, no normals.** The art's baked shading is the
   light direction. Time of day is colour only.
5. The option is active in daylight too: with level 0 the field is white
   outdoors and the result is the classic frame; indoors it is the roof dim.
   With the option off the classic path runs byte for byte as before: the
   extra target is never built.

## Consequences

- Cost: a grid of about 65x65 cells per frame, one line walk per light per
  cell inside its radius (cached), one triangle array for the visible land
  and one masked quad per visible tall static into a second small target.
  No shadow maps, no extra passes, no HDR. Measured numbers go with the PR.
- The masked static redraw is a second draw of the visible tall statics into
  the light target; the perf lane's draw-call budget is told.
- `AddLight` gains one guarded line (the realtime hook) and
  `PrepareLightsRendering` / `InitializeRenderTargets` one each (the mode
  gate); `RenderTargets.Draw` reads an override texture. All marked.
- A light held by a mobile moves every frame; its map is recomputed each
  frame it moves (289 cells, trivial).

## Validation

`client.realtime.lights` runs `RealtimeLightsProbe.tscn` headless: default
off and persistence, the ambient ramp end points, a wall blocking a light
and a door not blocking it, falloff to zero at the radius, the cache hit and
its invalidation, and the roof dim. The smoke carries the same line. Rendered
proof (a torch-lit room by night, a tavern by day, the classic frame with the
option off) is evidence with the PR, not inferred from the probe.

## ADR Dependencies

- ADR-0001 (presenter seam): traversal, sorting and the world draw are untouched.
- ADR-0023 (post-processing): stateless, nearest, composited in the one place.
- ADR-0027 amendment (Enhanced HD): the L1 render-option plug-in shape.

## Engine Compatibility

Godot 4.7.2 mono. Uses `SubViewport`, `RenderingServer.CanvasItemAddTriangleArray`
and `CanvasItemAddTextureRectRegion` with a canvas-item shader; nothing 3D, no
`PointLight2D`, no HDR 2D. Works on the Compatibility renderer.

## GDD Requirements Addressed

None registered; a client option under the feature-layer policy in AGENTS.md.
