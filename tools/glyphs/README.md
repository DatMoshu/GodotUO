# tools/glyphs

Builds the button glyphs the client draws for its prompts (the window menu,
the command bar's tab, Options > Controller buttons, tooltips; ADR-0025) from
Kenney's **"Input Prompts Pixel" 1.0** (CC0).

```
python tools\glyphs\run.py --source "<Input Prompts Pixel folder>"
```

`--source` is the folder holding `Tilemap/tilemap_packed.png` and
`License.txt`. It is never assumed: pass it, or set `KENNEY_INPUT_PROMPTS`.
The pack comes from www.kenney.nl (it is also in Kenney's "All-in-1" bundle,
under `Icons/Input Prompts Pixel`).

Output: `godot/GUO/src/Resources/embedded/glyphs/` (16x16 PNGs plus Kenney's
`License.txt`), embedded in the assembly by `GUO.csproj` as `glyphs/<name>.png`.
The output is committed; run this only to change the set.

What it does:

- copies the tiles named in `TILES` (face buttons A/B/X/Y, the View button for
  Back, D-pad, sticks, Esc,
  mouse and its buttons) by tile number;
- draws the four PlayStation symbols, which the pixel pack lacks, onto the
  pack's blank light disc (tile 12) in the pack's letter grey;
- copies `License.txt`.

Provenance and the tile list: `docs/upstream/KENNEY_INPUT_PROMPTS.md`.
Pillow is the only dependency.
