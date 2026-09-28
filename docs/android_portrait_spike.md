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
| 2 | Pre-game | login and character-select gumps at portrait width: `DpiScale`, whether the pre-game centring clips them | the login gump cannot be seen whole at any scale ≥ 1x |
| 3 | The world | world view size with `GameWindowFullSize`; tiles visible east-west and north-south vs landscape (the isometric diamond loses width) | fewer than ~9 tiles across the character's row |
| 4 | The command bar | cell = view width / `PerRow` (10): px and mm per slot in each orientation; three rows' height as % of the screen | a slot under ~7 mm, or three rows over ~30% of the screen |
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
