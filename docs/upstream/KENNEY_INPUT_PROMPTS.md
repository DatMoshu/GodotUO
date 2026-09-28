# Button glyphs: provenance and credits

The button prompts (`godot/GUO/src/Input/Glyphs`, ADR-0025) draw 16x16 glyphs
from **"Input Prompts Pixel" 1.0 by Kenney** (www.kenney.nl, 2021), released
under **CC0 1.0** (public domain). Kenney's `License.txt` is kept beside the
glyphs in `godot/GUO/src/Resources/embedded/glyphs/`. Credit is not required;
this note gives it anyway.

Only the glyphs the client uses are copied, by `tools/glyphs/run.py`, from
`Tilemap/tilemap_packed.png` in the pack:

| File | Tile | What |
|---|---:|---|
| `pad_a`, `pad_b`, `pad_x`, `pad_y` | 13-16 | Face buttons by printed letter (light set) |
| `dpad` | 34 | D-pad |
| `pad_back` | 616 | The View button (two overlapping squares), for Back / Select on every pad |
| `stick_l`, `stick_r` | 416, 484 | Left and right stick |
| `key_esc` | 17 | Esc key |
| `mouse`, `mouse_left`, `mouse_right` | 76-78 | Mouse, left and right button |

## Changes

The pixel pack has no PlayStation symbols. `ps_cross`, `ps_circle`,
`ps_square` and `ps_triangle` are the pack's blank light disc (tile 12) with a
6x6 cross, circle, square or triangle drawn on it by `tools/glyphs/run.py`, in
the pack's own letter grey. The shapes are generic geometric symbols drawn for
this project; no Sony artwork is used. They are released with the glyphs
under CC0.

No Unity Asset Store or other licensed glyph set is used.
