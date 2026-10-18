# tools/postfx

The post-processing stack's tooling (ADR-0023). `luts.py` generates the
built-in colour-grading LUTs (`godot/GUO/postfx/luts/*.png`) from one function
per look, so they are reproducible. `run.py sheet` logs a client into a
private ModernUO copy (never the shared shard), photographs and times every
shader and preset, checks an identity pass matches the world target pixel for
pixel, and makes a labelled contact sheet; `tour` records the looks as a
video. Output goes to `build/postfx_sheet/<time>/`.

Integer controls round and clamp to the shader's declared limits, including
values applied from saved JSON. During a stack rebuild the current world
texture is shown until the replacement viewport has had a frame to render.
An empty or failed build falls back to the world; Classic keeps returning
the original world texture.

## Run

```
python tools\postfx\run.py sheet [--port 2596] [--at 1475,1645] [--size 1280,800] [--out DIR]
python tools\postfx\run.py tour  [--port 2596] [--at 1475,1645] [--size 1280,800] [--out DIR] [--fps 30]
python tools\postfx\run.py luts
```

## Tests

`launchers/dev/smoke.bat` (or `.sh`) runs `PostFxEngineProbe.tscn` headlessly
with synthetic targets and a shader: integer rounding/clamping, slider JSON
type and live material updates, Classic texture identity, rebuild/toggle/resize/freed-viewport fallback,
missing shaders and disposed targets. No shard, client art or service is needed.
This checks texture selection and lifecycle, not rendered pixels; use `sheet`
for visual output. Both probes return 0 when their checks pass.
