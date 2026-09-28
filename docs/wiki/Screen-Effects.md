# Screen Effects

GUO can give the world a different look: cel shading, ink outlines, black and
white, sepia, noir, colour grades, a vignette, a glow around lights, an EGA or
handheld palette, CRT scanlines, and colour-blind help. Effects apply to the
**world only**, at the art's own pixels, before the window is scaled up.
Gumps, the paperdoll and the rest of the interface are never touched.

**Classic**, the default, is exactly ClassicUO's picture: no effect runs, and
the world is drawn the same way it always was. The decision record is
ADR-0023.

## Choosing a look

- **Options > Video > Screen effects...**, or **Ctrl+Shift+E** in game.
- Pick a look. Each pass has a toggle and its own settings. The sliders and
  choices come from the shader itself, so a new effect brings its own
  controls.
- **Compare with Classic** splits the world: Classic to the left of the gold
  line, the look to the right. Drag the slider to move the line.
- **Save as preset** keeps what you have under a new name.

The chosen look is remembered on this device.

The same things by typed command (a client command, like ClassicUO's own):

```
-postfx list          the looks and the shaders there are
-postfx load Noir     switch to a look
-postfx off           back to Classic
-postfx save My Look  save the current look under a name
-postfx split 0.5     compare with Classic (again to turn it off)
-postfx cost          how long the effects took on the GPU last frame
-postfx               open or close the menu
```

## The looks that come with GUO

Cel, Ink Outline, Posterize, Black & White, Sepia, Noir, Warm Grade,
Teal & Orange, Faded Film, Moonlight, Vignette, Glow, EGA, Handheld, CRT,
Colour Help (protanopia, deuteranopia, tritanopia), and two that react to the
game:
- **Blood Rush** narrows and reddens the view as you lose health.
- **Battle Focus** turns colder and harder in war mode.

On a desktop GPU each look costs well under a millisecond a frame. The
measured costs are in ADR-0023.

## Your own looks

Looks are plain text. Put them in the `postfx` folder of the client's home
(beside `settings.json`); they appear in the menu, and a file changed while
you play is picked up within a second.

```json
{
  "name": "Stormy",
  "description": "Cold and dim when it rains.",
  "passes": [
    { "shader": "grade",    "params": { "temperature": -0.3, "saturation": 0.8 } },
    { "shader": "vignette", "params": { "radius": 0.85, "strength": 0.3 } }
  ],
  "bindings": [
    { "pass": 0, "param": "exposure", "from": "weather", "scale": -0.15, "offset": 0.0 }
  ]
}
```

- A pass is a shader by name (a built-in, or your own `.gdshader` in the same
  folder) plus its settings. A setting is a number, `true`/`false`, a colour
  `[r, g, b, a]` (0..1), or an image path for a texture (a LUT or palette
  strip beside the preset).
- A binding drives one setting from the game every frame:
  `setting = offset + scale * value`, kept within the setting's range. The
  values are:
  - `hp_ratio` and `hp_missing` (health left or lost, 0..1);
  - `stamina_ratio` and `mana_ratio`;
  - `light_level` (0 bright .. 1 dark);
  - `war_mode` (0 or 1);
  - `weather` (0 none, 1 rain, 2 storm, 3 snow);
  - `time` (seconds).
- Nothing carries over from one frame to the next, so no effect can smear or
  shimmer the art.

## Your own shaders

A pass is a Godot `canvas_item` shader that reads the pass before it and
writes its result:

```glsl
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float amount : hint_range(0.0, 1.0, 0.01) = 0.5;   // becomes a slider
uniform int style : hint_enum("Soft", "Hard") = 0;          // becomes a dropdown
uniform vec4 tint : source_color = vec4(1.0);               // becomes a colour picker

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	COLOR = vec4(mix(c.rgb, c.rgb * tint.rgb, amount), c.a);  // keep the alpha
}
```

Declare `uniform sampler2D light_tex : filter_nearest;` to read the client's
light buffer (where torches, lamps and spells light the world). Keep sampling
**nearest**: UO's art is pixel art, and filtering it blurs it.

## Sharing looks

The Asset Store has a **postfx** kind: a pack of presets, their shaders and
any images they use. Installed packs appear in the menu beside the built-in
looks, and your own same-named look wins. See ADR-0019 (Amendment 3).
