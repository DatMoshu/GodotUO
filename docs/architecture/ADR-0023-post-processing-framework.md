# ADR-0023: Post-Processing and Shader Framework on the World Target

## Status

Proposed

## Date

2026-09-27

## Last Verified

Not yet: written before the code. The Validation section lists what must be
measured before this moves to Accepted.

## Decision Makers

Project owner (asked for it: "a post-processing and shader framework with a
modern effects menu"); GUOWeb (implements); `uo-render-engineer` (owns
`src/Render`, ADR-0001 to ADR-0006).

## Summary

Screen effects run as a **stack of stateless passes over the world render
target only**: one `.gdshader` per pass, each pass a `SubViewport` the size of
the world target, reading the pass before it. The stack's output replaces the
world texture in exactly one place, the composition in `RenderTargets.Draw`.
There the world, the lights and the UI are layered as upstream does, and the
nearest-neighbour upscale happens afterwards. Gumps and the UI are never
touched. **Classic, the default, is an empty stack that hands back the world
texture itself**, so the desktop stays 1:1 with ClassicUO by construction.
Looks are text presets (JSON) with bindings from game state, loaded from
`res://` and the client home, hot-reloaded, installable from the Store, and
edited in a card-style menu whose sliders come from each shader's uniform
hints.

## Context

- The client already renders the world into its own `SubViewport`
  (`RenderTargets.WorldRenderTarget`, `src/Render/RenderTarget2D.cs`, ADR-0001),
  at native pixel size. Lights go to their own target
  (`LightRenderTarget`) and are blended on top; the UI goes to a third. All
  three are layered in `RenderTargets.Draw`, a ported file.
- Rule 3 (parity before improvement) and the desktop's 1:1 promise: any
  effect has to be something a player turns on, never a default.
- Rule 7 (never filter pixel art): an effect may change a pixel's colour, but
  sampling stays nearest and 1:1 at the world target's own resolution.
- A community member asked that 2D art not shimmer: no temporal effects.
- Mobile (ADR-0017) and the web (ADR-0008) run the Compatibility renderer, on
  weaker GPUs.

## Decision

### 1. Where it runs

One call in `RenderTargets.Draw` (a marked `PORT DEVIATION`): the texture
drawn for the world becomes `PostFx.Process(world, light)`. With no enabled
pass, `Process` returns `world` unchanged: same texture, same draw, same
pixels. The light target is still blended by upstream's code afterwards; an
effect that wants light reads it as an input and does not replace that blend.

### 2. The pass stack

- A pass is a `.gdshader` (`shader_type canvas_item`) plus uniform values.
  Its input is `source` (the previous pass's output, or the world target),
  with `filter_nearest`. Optional inputs are named uniforms the framework
  fills when the shader declares them (section 3).
- **One** post-processing `SubViewport` the world target's size (transparent
  background, nearest filtering, `UpdateMode.Always`, 2D only) draws the
  world texture. Then, per enabled pass, it draws a `BackBufferCopy` of the
  whole viewport and a full-size `ColorRect` carrying the pass's
  `ShaderMaterial`. The shader reads the copy as `source`
  (`hint_screen_texture, filter_nearest`) and writes with
  `render_mode blend_disabled`. The passes therefore run in **draw order
  inside a single viewport**, which settles two things a chain of
  viewports would leave to chance:
  - no reliance on the order Godot renders sibling viewports in;
  - a world target recreated on resize can never make a pass read last
    frame's texture. The stack is rebuilt whenever the world target is
    replaced, so it is always created after the target it reads.
- The memory cost is one world-sized target, whatever the number of passes.
  A preset is capped at 8 passes to bound the GPU time.
- Alpha is preserved: the world target is transparent where nothing is drawn
  (the canvas background shows through, ADR-0016), and every starter shader
  passes `source.a` through.
- **Stateless:** no pass reads a previous frame, not even its own. No
  `TIME`-driven noise either, except where a preset binds time explicitly
  (for example a CRT roll, off by default). The same world frame gives the
  same output.

### 3. Auxiliary inputs, decided per effect

| Uniform a shader may declare | Filled with | When |
|---|---|---|
| `light_tex` | `LightRenderTarget`'s texture | now (bloom from lights) |
| `palette_tex` | a palette strip from the preset (LUT or EGA/handheld palettes) | now |
| `lut_tex` | a 3D LUT laid out as a strip (N*N x N) | now |
| `id_tex` | an object-id mask (PixelPicker's idea: one colour per game object) | later, when an effect needs object outlines |
| `depth_tex` | the isometric depth the batcher already computes per sprite | later |

The framework creates a buffer only when an enabled pass declares it, so
Classic pays nothing.

### 4. Presets are text

```json
{ "name": "Noir", "passes": [
    { "shader": "grayscale", "params": { "strength": 1.0 } },
    { "shader": "vignette",  "params": { "radius": 0.75, "softness": 0.35 } } ],
  "bindings": [ { "pass": 1, "param": "strength", "from": "hp_missing", "scale": 0.8 } ] }
```

- Built-ins live in `res://postfx/presets/`, shaders in `res://postfx/shaders/`.
  User presets are in the client home's `postfx/` folder (the home is
  `GuoDataDirectory`, as settings.json), and **user shaders** (`*.gdshader`)
  there too.
- **Hot reload:** the user folder is polled once a second (file time); a
  changed preset or shader is reloaded in place.
- **Console:** `postfx list`, `postfx load <name>`, `postfx off` (Classic),
  `postfx save <name>`, typed like the client's other `-` commands.
- **Bindings** drive a parameter every frame from game state:
  `hp_ratio`, `hp_missing`, `stamina_ratio`, `mana_ratio`, `light_level`
  (0 bright .. 1 dark, from `World.Light`), `war_mode` (0/1), `weather`
  (0 none, 1 rain, 2 storm, 3 snow), `time` (seconds, opt-in). A binding is
  `param = offset + scale * value`, clamped to the uniform's hint range.
  Players script their look with no code.
- **The Store** (ADR-0019) gains an asset kind `postfx`: a zip of presets and
  shaders, installed into the home's `postfx/` folder under the same
  allowlist rules as other kinds (only `.json` and `.gdshader`, no paths
  outside the folder). A short amendment note goes into ADR-0019.
- The chosen look is remembered in the home's `postfx/state.json` (one per
  device, like the canvas background's device settings), with Classic as the
  default. It is not stored in the ported profile, so the profile format stays
  upstream's.

### 5. The menu

A modern Godot card in the style of the window menu and the Store (not a
gump), opened from Options and by a hotkey. It holds:

- a preset picker;
- the pass list, with a toggle per pass;
- for each pass, sliders generated from the shader's uniforms. A `float`/`int`
  uniform with `hint_range(a, b, step)` becomes a slider, a `bool` a toggle,
  a `vec4 : source_color` a colour picker. Uniforms without hints are
  not shown;
- an **A/B split** slider: the left of the split shows Classic, the right the
  effect. It's done in a final framework pass, so it never touches the
  shaders;
- **Save as preset** (to the home's `postfx/`).

It never takes focus from the game when closed, and the UI (gumps) is never
under an effect, so the menu can always be read.

### 6. Starter effects

posterize / cel, outline (luma and colour edges), black & white / sepia /
noir, colour grading with LUTs, vignette, bloom from the light target, a
palette remap (16-colour EGA, 4-colour handheld) with ordered (Bayer)
dithering, CRT scanlines, colour-blind daltonize (protanopia, deuteranopia,
tritanopia).

### 7. Performance budget and tiers

- Desktop: at most **1.0 ms** of GPU time per pass at 1080p, and 3 ms for a
  whole preset. Each pass's cost is measured by the proof run and listed in
  Validation.
- Mobile and web tier (`OS.HasFeature("mobile")` or `web`): presets whose
  passes are marked `"heavy": true` (bloom, outline with a wide kernel, CRT)
  are offered but flagged, and the stack may run passes at half resolution on
  those tiers (upscaled nearest). Classic costs nothing anywhere.

## Alternatives rejected

- **A `CanvasLayer` with a screen-reading shader (`SCREEN_TEXTURE`) over the
  whole window.** It would also run over gumps and the UI, it runs after the
  upscale (so it filters the scaled image, not the art's own pixels), and it
  cannot tell the world from the interface.
- **Godot's `WorldEnvironment` glow and tonemap.** They are 3D-renderer
  features, not available to a 2D canvas under Compatibility, and they're not
  per pass.
- **Temporal effects (TAA, motion blur, feedback trails).** Rejected on the
  shimmer concern; nothing is carried between frames.
- **Editing the batcher's hue shader.** It would mix effects into every
  sprite's draw and lose the "Classic = the same texture" guarantee.

## Consequences

- One marked line in `RenderTargets.Draw`, one entry in the Options gump (a
  button that opens the card), one console command. Everything else is new
  GUO-owned code: `src/Render/PostFx/`, `res://postfx/`.
- VRAM: one world-sized target while any look other than Classic is on (about 8 MB at 1920x1080), none for Classic.
- Screenshots from `launchers\dev\screenshot.bat` show the effect; render
  dumps (render_diff) are unaffected, since they record objects, not pixels.

## Validation (to do before Accepted)

1. Classic is unchanged. A pixel comparison of the same frame with the
   framework off and with Classic selected shows 0 differing pixels, and
   render_diff between the two dumps is clean.
2. A screenshot sheet of every starter effect at the same spot on a private
   shard.
3. The GPU cost per pass (1080p desktop), measured with
   `RenderingServer.viewport_get_measured_render_time_gpu` on each pass's
   viewport.
4. The smoke is green and port_drift `--strict` is 0.

## Related

ADR-0001 (render presenter seam), ADR-0016 (canvas background), ADR-0017
(Android), ADR-0008 (web), ADR-0019 (Store).
