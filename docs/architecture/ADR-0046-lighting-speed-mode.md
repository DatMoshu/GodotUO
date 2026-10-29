# ADR-0046: Lighting speed mode, a screen-space surface buffer with per-pixel lights on the Depth World

Status: Proposed, 2026-10-29 (owner request in task 959: realistic lighting, shadows, GI, water and weather that
run "lean and fast"; the lighting sprint of 2026-10-29 reached 65 ms a frame at the zoomed-out view and is parked
on `lighting/integration-20261029`). Needs Moshu's decision before the option ships on; the branch is
`lights/speed-20261029`.

## Context

The tile-grid light field of ADR-0039 lights the world on the CPU: a field of 4 x 4 samples per tile, a
software-rasterised shadow mask, and tile-space passes for soften and bounce. At camera Zoom 2 (the owner's
zoomed-out view, 1920 x 1080) it costs 65 ms against Classic's 2.5 ms, and its floor shadows clip, float and
stair-step (the owner's recordings of 2026-10-29).

A community ClassicUO build (L's "HD-2D") reaches 60 fps with 2 ms of world draw by doing the same job on the GPU
per pixel: a surface buffer that records every pixel's world position and surface class, one additive pass per
light that reads it, an edge-aware blur, and the classic darkness multiply. Rain, puddles and reflections then
read the same buffer. The lead's research notes are in the evidence run `20261029_guo-lights-vfx-weather-water_l-vfx`
(`data/RESEARCH.md`). Nothing is copied from that build; the design below is re-derived for GUO.

GUO already owns the two halves this needs: the Depth World (ADR-0044) keeps every visible world quad in GPU
storage buffers and draws them with ClassicUO's depth rules, and the light field (ADR-0039) computes, caches and
proves per-light tile visibility with wall-edge lines.

## Decision

**Lighting speed mode** is an L1 client option, off by default. Off is byte-identical to Classic: no buffer, pass
or pipeline below is created. It requires the Depth World (a RenderingDevice renderer); on Compatibility, or when
the Depth World falls back to the canvas for a frame, the option is shown disabled with the reason and the frame
draws Classic lighting.

### 1. The surface buffer (G-buffer)

A second pass over the Depth World's own resident and dynamic quad buffers, with a surface pipeline, into a
GUO-owned target the size of the world target:

- Format `R16G16B16A16_SFLOAT`: `xy` = world tile position (continuous, tile units), `z` = world Z (UO units),
  `w` = class and flags packed as `class + 16 x flags` (integers, exact in half float below 2048).
- Its own `D32_SFLOAT` depth, LessEqual, depth write on, the same depth value the colour pass uses, so the
  buffer has the colour picture's occlusion. Alpha-zero texels are discarded as in the colour pass.
- Classes: `0` none, `1` floor, `2` wall face along X (shows its +Y face, "south"), `3` wall face along Y
  (shows its +X face, "east"), `4` upright card, `5` sloped roof, `6` water.
- Flags: `1` covered (a roof or floor above this tile at this Z, the field's cover grid), `2` wet surface
  candidate (tiledata or allowlist), `4` window opening, `8` translucent.
- World position per corner follows one rule per class (the lead's notes, section 2.1): the floor plane at the
  quad's Z (stretched land per corner), the wall plane one tile past the piece's tile on the shown face's axis,
  a card through the foot for uprights, the neighbour-sloped plane for roofs. The wall face comes from
  `RealtimeWallFaces` (art silhouette), resolved by neighbours when ambiguous; a wall's top Z rides along so
  pixels above it read as floor (lit caps).
- Per-quad surface data lives in the resident record's spare `aux.w` (packed) and, for dynamic occurrences, in
  a parallel 8-byte storage buffer uploaded with the state rows. The colour pipeline, its records and draw order
  are not changed; the surface pass binds the same buffers with a second pipeline, after the colour draw list.

A debug view (`speedmode gbuf` chat command, and the `--lighting-speed-probe` dump) paints the classes red,
green, blue, white, with cover hatched, so a reviewer can see what the renderer sees.

### 2. The light pass

One screen-aligned quad per light, bounded by 1.6 x the light's screen radius, additive into an
`R16G16B16A16_SFLOAT` light target cleared to the ambient colour. The fragment reads the surface buffer and
computes

`light = colour x energy x flicker x falloff(d) x facing x visibility x shadow`

- `d` in tiles from the light's world position (lamp height tile Z + 10, carried lights at the mobile's
  interpolated position), `falloff` the profile's curve (ADR-0039 amendment: A..E or custom), default
  `(1 - d^2 / r^2)^2`.
- `facing`: walls `max(0, dot(face normal, to light))` with a 0.2 floor so a lit wall never goes black beside a
  lit floor; uprights lit from the front, dimmed from behind; floor 1.
- `visibility`: the light field's cached reach map (`RealtimeLightField.ComputeReach`, walls and edges as today)
  uploaded once per light per change as an `R8G8` tile texture (exact, dilated by one), feathered 1/8 tile.
  No full-grid field is computed in this mode.
- `shadow`: section 3.
- Lights are the classic `AddLight` offers (the 100-offer cap), plus spell bursts when that option is on.
- Ambient: the shard light level through the existing ramp (`RealtimeLightField.AmbientFor`), with a night
  floor option (default 3%) and the roof dim (`RoofDaylight`) for covered pixels. The ramp keeps true dark at the
  classic level the shard sends; it is not re-balanced to another client's level.

### 3. Shadows

- **Lamp shadows**: per light, for the nearest N lamps (default 6, option 0..10), a polar shadow map row
  (angle across, elevation down, R32F, min-blended caster distance) rasterised on the RenderingDevice from caster
  cards (tall statics, mobiles, items) and wall strips on their wall plane. A mobile's caster card uses the
  animation frame for the direction from the light to the mobile. Height toward the light is capped so a body
  keeps its proportions. The light pass compares the pixel's polar distance with bias and a softness in tenths
  of a tile; shadowed pixels keep 15% of that light. Roofs, floors, surfaces, stairs and lying items never cast.
- **Sun shadows**: the sprint's projected silhouettes, direction from the shard clock, rasterised by the
  RenderingDevice mask (`ProjectedShadowMaskRd`), read by the light pass as one more multiplier on the ambient
  term for floor pixels. One max mask: silhouettes never stack.

### 4. Window light

A JSON component `guo/window-light@1` (data_formats section 50) maps a window graphic to an opening shape (rect
with pane rows and columns, round arch, pointed arch, oval, none), an aspect, and the floor patch in tiles.
By day (ambient above a threshold) a window on a wall whose inside tile is covered and whose outside tile is open
draws its patch as a parallelogram into the light target, bars cut out, sun colour, on floor-class pixels inside
the patch and the lower part of upright cards standing in it. The table ships with the client (built-in set from
the owner's contact sheets), and a pack may extend it (L2).

### 5. Soften, compose, weather, water

- One edge-aware blur of the light target (0..10 steps, default 3) that stops at class edges read from the
  surface buffer. Labelled **Soften (approximate)**, never GI.
- The light target replaces the classic light target at the existing seam (`RenderTargets.LightOverride`),
  multiplied with the classic darkness blend. Nothing else in the frame moves.
- Rain in this mode is one batched pass from a procedural drop sheet (no art): streaks, splashes on floor
  pixels, drips at roof edges; every drop and splash is cut where the surface buffer's cover flag is set.
  Wetness eases from the weather packet (rain count / 70, rising over 60 s, draining over 180 s). Wet floor
  pixels darken and glint toward each light; puddle shapes grow on an allowlist of floor types; a mirrored
  copy of each upright card is drawn below its foot in wet pixels, darkened, as a reflection. Fog is a screen
  pass from world distance and height with wind-scrolled noise. The "lighting" water style (ADR-0037) binds the
  light target instead of the field texture.

### 6. Options

Profile fields, all `PORT DEVIATION (GUO)` and default off unless noted; Options page "Lighting" group with the
labels shown:

| Field | Label | Default |
|---|---|---|
| `LightingSpeedMode` | Lighting speed mode (per-pixel lights on the GPU, needs the Depth World) | off |
| `SpeedModeNightFloor` | Night floor (%) | 3 |
| `SpeedModeSoften` | Soften (approximate), steps | 3 |
| `SpeedModeLampShadows` | Lamp shadows, lights with shadows | 6 |
| `SpeedModeSunShadows` | Sun and moon shadows | on |
| `SpeedModeWindowLight` | Window light by day | on |
| `SpeedModeWeather` | Rain, puddles and wet surfaces | on |
| `SpeedModeFog` | Fog | on |
| `SpeedModeReflections` | Reflections in wet ground | on |

Sub-options apply only while the mode is on; their defaults are on so that one switch shows the whole look.
The mode never changes picking, movement, packets or the UI.

## Consequences

- Cost target: at camera Zoom 2, 1920 x 1080, Britain bank, quiet fixture, the same Debug build the owner plays,
  the mode within about 25% of Classic frame time. The surface pass is a second draw of about 5k quads; the light
  pass a few million additive fragments; the blur two passes; all GPU. CPU adds the surface rows and the per-light
  reach uploads.
- Parity: off is proven by a 0 px capture diff and unchanged draw-call counts. The colour pipeline of ADR-0044
  is untouched; only a second pipeline over the same buffers is added.
- The sprint's tile-space GI, CPU shadow mask and field geometry are not carried into this mode.
- Each slice has a headless probe and a scenario line (`lighting_speed`, `lighting_speed_shadows`,
  `lighting_speed_weather`) and a data_formats section (49 surface buffer and light pass, 50 window light
  table, 51 weather state).

## Validation

Filled in by the slices' heads: probe logs, the off-state capture diff, the Zoom 2 frame-time table, and stills
of the debug view, lamp shadows, window patches, rain occlusion and reflections at the bank.

## ADR Dependencies

- ADR-0044 (Depth World): the resident and dynamic quad buffers, the depth contract and the canvas fallback the
  surface pass rides on. The colour pipeline is not modified.
- ADR-0039 (Realtime lights): the light offers, profiles, curves, the reach cache and wall-face classifier, the
  `LightOverride` seam in `RenderTargets.Draw`.
- ADR-0037 (Water render modes): the "lighting" water style binds this mode's light target.
- ADR-0027 (world render modes) and ADR-0023 (post-processing): how an L1 render option plugs in; nearest
  sampling and the world draw stay unchanged.
- ADR-0001 (renderer seam): the mode lives under `src/Render/` behind the presenter seam.

## Engine Compatibility

Godot 4.7.2 mono, RenderingDevice (Forward+ or Mobile). Uses `RenderingDevice.TextureCreate`,
`FramebufferCreate`, `RenderPipelineCreate` with a second colour attachment format, storage buffers and push
constants exactly as `OrderedWorldDevice` does today (`docs/engine-reference/godot/`). Half-float colour targets
and R32F are checked with `TextureIsFormatSupportedForUsage` at init; an unsupported format disables the option
with that reason. Compatibility has no RenderingDevice: the option is disabled there.

## GDD Requirements Addressed

Owner request, task 959 (2026-10-29): realistic lighting, shadows, GI, improved water, simulations and presets
beyond current GUO, lean and fast, original look preserved, enhancements opt-in, technique and limits stated
honestly. No TR id exists for lighting; `tr-registry.yaml` is unchanged.
