# tools/postfx

The post-processing stack's tooling (ADR-0023). `luts.py` generates the
built-in colour-grading LUTs (`godot/GUO/postfx/luts/*.png`) from one function
per look, so they are reproducible. `run.py sheet` logs a client into a
private ModernUO copy (never the shared shard), photographs and times every
shader and preset, checks an identity pass matches the world target pixel for
pixel, and makes a labelled contact sheet; `tour` records the looks as a
video. Output goes to `build/postfx_sheet/<time>/`.

## Run

```
python tools\postfx\run.py sheet [--port 2596] [--at 1475,1645] [--size 1280,800] [--out DIR]
python tools\postfx\run.py tour  [--port 2596] [--at 1475,1645] [--size 1280,800] [--out DIR] [--fps 30]
python tools\postfx\run.py luts
```

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed. Exit 0 when the probe passed.
