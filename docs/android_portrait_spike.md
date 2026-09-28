# C9 — portrait on the Odin: what the spike measures

A spike, not a feature. The owner is sceptical, so the output is a table
of measurements and a keep/kill verdict, not a portrait mode. Nothing it
adds ships on by default.

## Where things stand

- `project.godot` pins `window/handheld/orientation=4` (sensor landscape,
  ADR-0017). Portrait never happens today.
- A rotation reaches the client as a window resize: the viewport's
  `size_changed` → `GameController.WindowOnClientSizeChanged`, the path
  ClassicUO takes for `SDL_EVENT_WINDOW_RESIZED`. No new plumbing is needed
  to *get* a tall window; the question is what copes with one.
- Flags are baked into the APK at export (`command_line/extra_args`), and an
  export is a heavy job. So the spike rotates **at runtime** from one build:
  `--portrait-probe` calls `DisplayServer.ScreenSetOrientation` to portrait,
  measures and screenshots, returns to landscape and measures again, then
  exits. One export and one device run cover both.

## What it measures (each with a screenshot, landscape and portrait)

Tonight's cap: one Odin run, the five rows with a kill column (1–5). Rows
6–8 run only if that run's verdict is **keep as an option**. The table and
the screenshots go to `build/` for a post drafted in the morning.

| # | Question | Measure | Kill if |
|---|---|---|---|
| 1 | Does the rotation itself work? | `ScreenSetOrientation` flips the surface; `size_changed` fires once; time to the first frame after it; textures intact (no lost GL context) | the surface is recreated and the atlases are lost |
| 2 | Pre-game | login and character-select gumps at portrait width: `DpiScale`, whether the pre-game centring clips them | the login gump is too big for the screen at any scale ≥ 1x (placed off it is a fix, not a kill) |
| 3 | The world | world view size with `GameWindowFullSize`; tiles visible east-west and north-south vs landscape (the isometric diamond loses width) | fewer than ~9 tiles across the character's row |
| 4 | The command bar | cell = view width / `PerRow` (10): px and mm per slot in each orientation; three rows' height as % of the screen | a slot under ~7 mm wide or ~5 mm tall (the plate's art scale follows the cell width, so a narrow screen shrinks it both ways), or three rows over ~30% of the screen |
| 5 | Gumps | for paperdoll, backpack, Options (classic and Modern), Skills, Spellbook, world map: fits width at 1x? what `GumpPresentation` fit gives; which spill off the side | the everyday gumps (paperdoll, backpack, Modern views) need a sideways scroll |
| 6 | Saved positions | gumps saved in landscape, reopened in portrait: on screen, or off the right edge? and back | (a cost, not a kill: a clamp on rotate) |
| 7 | Soft keyboard | share of the screen the IME covers in portrait; journal/chat entry still visible | — |
| 8 | The pad | D-pad directions stay screen-relative after the rotation (the joypad is unrotated) | — |

## Verdict

The spike ends with the table, the screenshots, and one of: **kill** (a kill
column tripped); **keep as an option** (an Options toggle and the work to
make it hold, costed from rows 5–6); or **not worth it** (it works but gains
nothing a player would pick it for). No ADR unless the verdict is keep.

## Cost of the spike

Rows 1–5: the probe (~150 lines under `src/Bootstrap`, no ported file
touched), one export, one Odin run inside a heavy-job slot. Rows 6–8 add
about half a day, and only on a keep. The Odin is a
single screen, so the Thor's dual-screen path (`DualScreen.ShelfOn`) stays out
of scope.

## Result — Odin 2 Mini, 2026-09-28

One run, rows 1–5 (`launchers\android\portrait_probe.bat`). Photos and the
table: `build/android/portrait/`.

| # | Measure | Landscape | Portrait | |
|---|---|---|---|---|
| 1 | the rotation | — | flipped in 95 ms, one resize, atlases intact | ok |
| 2 | the login gump | fits, 640x480 in 960x540 at 2x | fits in size (640x480 in 719x1279 at 1.5x) but keeps its landscape place, off the right edge | ok (a fix: centre on resize) |
| 3 | world tiles, across x down | 15.3 x 8.6 | 11.5 x 20.4 | ok |
| 4 | command bar slot, w x h | 13.2 x 7 mm, three rows 28 % | 7.4 x 2.3 mm, three rows 5 % | **KILL** |
| 5 | scale to fit the width | paperdoll 3.66, backpack 3.40, Modern Options 1.03, Modern Skills 1.36 | paperdoll 2.74, backpack 2.55, Modern Options 1.04, Modern Skills 1.04 | ok |

**Verdict: kill, by the rule set before the run.** Row 4 trips it: ten
slots across 1080 px leave a 108 px cell, the bar's art scale is chosen
from the cell's width (`TouchGumpBar.ComputeLayout`, 64 px per step), so it
drops to 1x and a plate is 2.3 mm tall — the labels overlap and a thumb
cannot hit one. Everything else held: the rotation is a plain resize, the
world is playable (and shows twice as far north-south), and the everyday
gumps fit.

What reversing it would take, if the owner wants portrait anyway: a portrait
command bar (five slots a row, or an art scale from the screen's height),
and the pre-game gumps re-centred on a resize. Rows 6–8 (saved positions,
the IME, the pad) were not run, as agreed for a kill.
