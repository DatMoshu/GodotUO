# tools/editor_smoke

Proves the GUO editor addon (`godot/GUO/addons/guo_editor`) works in the real
editor, against the real client install. `docs/editor_plan.md` §5 makes this
the verification for every editor phase; phase 0 is what it checks today.

```
python tools\editor_smoke\run.py                 headless: every check, no screenshots
python tools\editor_smoke\run.py --windowed      an editor window, with screenshots
python tools\editor_smoke\run.py --reload        also rebuild and hot-reload the C#
python tools\editor_smoke\run.py --art 0x0E75    which static to search for
launchers\dev\editor_smoke.bat [same flags]
```

## What it does

1. `dotnet build` of `godot/GUO/GUO.csproj`. The addon is C# in the game
   assembly, so an unbuilt assembly means no addon.
2. Starts the pinned editor (`godot-console`, so it blocks and logs) with
   `-- --guo-editor-smoke <out>`. That flag makes the plugin add
   `EditorSmoke`, which does the checking from inside the editor:
   - the **UO Assets** and **UO Inspector** docks are in the editor tree;
   - the client data loaded through the ported `UOFileManager`, configured
     the same way the launchers configure it (environment, then
     `launchers\_shared\config.bat`);
   - then, for every tab of the Assets dock (Art, Gumps, Anims, Hues,
     Multis, Cliloc, Sounds, Maps, Parity): the tab is brought to the front,
     searched for its `SmokeQuery` (Art uses `--art`), and the UO Inspector
     must receive that panel's result: an image with non-transparent pixels
     (text only for Cliloc and Sounds), and text. Sounds also plays the
     selection through the game's audio. Parity is skipped, not failed, when
     UOWW's `uoasset` CLI is not installed;
   - windowed only: the editor window is captured once per panel;
   - then the **UO World** tab (ADR-0015), reached the way a user reaches it,
     through the Maps panel's "Show in UO World" for map0 1496,1628. The world
     must boot, `GameScene` must draw objects (and windowed, a frame with more
     than 64 colours), and a pick at the view's centre, through the game's own
     picking, must reach the inspector. A multi is placed and removed through
     the server's object and delete paths, and must add to what is drawn;
   - then the **world project overlay** (ADR-0011), in a project under the
     output folder: block 187,203 becomes water with three extra statics,
     written to JSON, reopened from disk and applied. The chunk must hold
     exactly those, closing must restore the install's block, and the
     install's `map*`/`statics*`/`staidx*` files must keep their modification
     times.
   - then the **asset overlay** (ADR-0020), before the World tab boots, in
     its own project under the output folder, with fixtures drawn at test
     time (never read from the install): a land tile (0x0244), a static
     (0x0E75), a gump (0x0064) and a hue (33) are imported. The loaders must
     return exactly the saved images, a wrong-sized land tile and an
     oversized static must be refused, Revert must bring the install's static
     back, the UO Inspector must say "replaced", the World tab's own loaders
     must return the imported static, and the install's `art*`, `gump*`,
     `hues*` and `verdata*` files must keep their modification times.
3. After the editor exits, with that asset project (`tools\world` and
   `tools\editor_asset_roundtrip`):
   - `export` and `verify` pass, and `verify` fails on a copy with one colour
     changed;
   - an export with `--out` under `UO_CLIENT_DATA` is refused and nothing is
     created there;
   - `pack` writes a zip without a single install-derived file, and it
     exports and verifies from wherever it is unzipped;
   - a headless client with `files_override` on the export decodes every
     asset unchanged, and a control run without it does not.
4. With `--reload`: after the first pass the addon writes `reload.request`;
   the tool touches a source file, rebuilds, and answers `reload.go`; the
   addon sends the editor the focus-in notification GodotTools reloads on.
   The reload recreates the plugin, which builds its docks again, and the
   checks run a second time. The editor log must show
   `Assembly load context unloaded successfully` and no
   `Failed to unload assemblies`. This is open question 1 of the plan, kept
   as a regression check.
5. Restores `project.godot` if the editor rewrote it. The windowed editor
   rewrites that file on exit (dropping its comments) with or without the
   addon; a smoke run must not leave the tree dirty.

## Output

`build\editor_smoke\<mode>\`, where mode is `windowed`, `headless`,
`windowed_reload` or `headless_reload`:

| File | What |
|---|---|
| `report.json` | every check, `ok`, `failures`; with `--reload`, the first pass under `before_reload` |
| `<panel>.png` | what the inspector was given for that panel, as decoded (`art.png`, `anims.png` is frame 0, `parity.png` is reference / GUO / diff) |
| `editor_<panel>.png` | the editor window with that panel showing (windowed only) |
| `world.png`, `world_multi.png`, `world_overlay.png` | the World tab's viewport: as the install has it, with the server-path multi, with the overlay (windowed only) |
| `editor_world.png` | the editor with the World tab showing |
| `world_project/` | the overlay check's world project: `project.json` and `blocks/0/187_203.json` |
| `*_after_reload.*` | the same, from the second pass (`--reload`) |
| `editor.log` | the editor's stdout/stderr |

These are renders of client art: they stay under `build\` and are never
committed (AGENTS.md rule 8).

## Headless vs windowed

**Headless is the default.** A window takes the desktop's focus from whoever
is working at the machine while it runs (the owner asked for this), so
`--windowed` is for when they agree. `--headless` is still accepted and does
nothing.

`--headless` runs Godot's own headless mode: no display, and nothing is
rendered, so there is no frame to capture; every check except the screenshots
still runs. The windowed mode opens an editor window for about a minute
(two with `--reload`). It needs a desktop session but no input.

Exit codes: 0 every check passed, 1 a check failed, 2 the editor could not
be started or timed out (600 s).

## F3 search (Phase 4b)

F3 (rebindable: Editor Settings > Shortcuts, `guo_editor/search`) opens a search over the editor's menus, settings and
screens, the GUO commands, and the UO data the loaders have open (art by name or id, gumps, hues, multis, bodies,
sounds, cliloc text, places). Code: `godot/GUO/addons/guo_editor/Search/`; one provider class per source. Named
places live in `Search/places.json` (the client data has no town names). The smoke queries "backpack", "0x0E75",
"3701", "statics", "grid", a coordinate, "project settings", "bp" and a typo, asserts the top result's kind, runs two
harmless entries, and (windowed) saves `search_backpack.png`. The recent-use history is in the editor's project
metadata, not in a tracked file.
