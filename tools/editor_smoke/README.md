# tools/editor_smoke

Proves the GUO editor addon (`godot/GUO/addons/guo_editor`) works in the real
editor, against the real client install. `docs/editor_plan.md` §5 makes this
the verification for every editor phase; phase 0 is what it checks today.

```
python tools\editor_smoke\run.py                 windowed: checks + screenshot
python tools\editor_smoke\run.py --headless      no window: checks only
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
   - searching the Assets dock for the art id selects it, and the inspector
     received it and decoded non-transparent pixels;
   - windowed only: the editor window is captured.
3. With `--reload`: after the first pass the addon writes `reload.request`;
   the tool touches a source file, rebuilds, and answers `reload.go`; the
   addon sends the editor the focus-in notification GodotTools reloads on.
   The reload recreates the plugin, which builds its docks again, and the
   checks run a second time. The editor log must show
   `Assembly load context unloaded successfully` and no
   `Failed to unload assemblies`. This is open question 1 of the plan, kept
   as a regression check.
4. Restores `project.godot` if the editor rewrote it. The windowed editor
   rewrites that file on exit (dropping its comments) with or without the
   addon; a smoke run must not leave the tree dirty.

## Output

`build\editor_smoke\<mode>\`, where mode is `windowed`, `headless`,
`windowed_reload` or `headless_reload`:

| File | What |
|---|---|
| `report.json` | every check, `ok`, `failures`; with `--reload`, the first pass under `before_reload` |
| `art.png` | the art the inspector decoded, as decoded |
| `editor.png` | the editor window, first pass (windowed only) |
| `editor_after_reload.png` | the editor window after the reload (windowed `--reload`) |
| `editor.log` | the editor's stdout/stderr |

These are renders of client art: they stay under `build\` and are never
committed (CLAUDE.md rule 8).

## Headless vs windowed

`--headless` runs Godot's own headless mode: no display, and nothing is
rendered, so there is no frame to capture; every check except the screenshot
still runs. The windowed mode opens an editor window for about twenty
seconds. It needs a desktop session but no input.

Exit codes: 0 every check passed, 1 a check failed, 2 the editor could not
be started or timed out (600 s).
