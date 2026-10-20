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
   light field. The sum has no ceiling; a cell brighter than white is
   scaled by its brightest channel when read, so a torch's orange stays
   orange at the flame instead of clipping channel by channel to yellow.
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
   light direction. Time of day is colour only. (Amended 2026-10-20: shadows
   from lights and the ambient, below.)
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
- A light held by a mobile moves every frame. Visibility is cached on the
  tile grid; falloff uses the sprite's interpolated sub-tile position each
  frame, so a carried pool does not wait for the next completed tile.
- A moving spell offers its own position and light graphic, including after
  its source becomes coordinates; the classic source-entity offer remains
  unchanged when the option is off.

Owner review follow-up (2026-10-19, director relay): pools must remain distinct
around their carriers rather than washing the whole plaza orange. Radius uses
the light sprite's width (`width / 44` tiles), with energy 2.2, accounting for
the effective footprint after corner averaging and falloff.
The ambient colour ramp is normalized to the classic grey's luminance, keeping
the same night level equally dark in the ON/OFF comparison. Untinted lamps use
a warm flame tint. These adjustments affect only the enabled option.

Night quality pass and shadows (2026-10-20, owner brief for the night seat):
make the pools look right, cast shadows from the real-time lights, and let
those replace UO's static shadows with an opt-out. This amends decision 4.

- **Pools.** Each light is evaluated at every tile corner (its own distance,
  the mean visibility of the four cells) and at the tile centre, and land is
  four triangles fanned from the centre: pools are round and peak at their
  light instead of being averaged a tile wide. Radius becomes `width / 54`
  tiles (exact corners no longer shrink the footprint). An uncoloured light
  is warm white, not the torch's orange, so a lantern and a torch differ as
  they do in classic. Unhued lights flicker a few percent. A walking light
  crossfades the visibility of the two tiles it is between.
- **Day.** Light scales with the ambient's headroom, so a torch at noon adds
  nothing (classic draws no lights by day). Roof sprites, what stands on a
  cover, and a building's outer edge take the open ambient; the indoor dim
  is for what is under the roof. With the option on at level 0 the frame
  again matches classic apart from interiors seen through doors and windows.
- **Shadows (Realtime shadows, on under the option; Keep classic shadows,
  off).** Trees, foliage, rocks and tall statics cast sprite shadows into two
  masks the land reads: the ambient's, in the classic shape and 40 % share
  (by day the classic shadow, redrawn in the light), and each light's, the
  sprite laid on the ground away from that light, its length growing with
  distance, the light left sampled on a 3x3 grid. Trees and thin posts no
  longer block the tile grid, which cut blocky wedges out of the pools. The
  classic static shadows step aside unless kept, and so do the mobiles':
  `MobileView` hands the frame it would have shadowed to the realtime pass
  (one marked branch), so a walker casts a shadow away from every torch near
  them. A standing static in the open is lit brighter on the side toward a
  light. The world draw changes only by those gates.

Cost at the Britain bank street, editor, 1280x720: 2.9 to 3.4 ms a frame for
grid, field, geometry and shadows together (25 casters, up to 8 lights).

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
