# tools/pixelorama

Pixelorama is GUO's pixel-art editor (ADR-0029). It is third-party and
fetched, never committed; this folder holds only the entry point and GUO's own
extension.

| | |
|---|---|
| **Upstream** | <https://github.com/Orama-Interactive/Pixelorama> |
| **Licence** | MIT ("Copyright (c) 2019-present Orama Interactive and contributors"), checked in its `LICENSE` at the pinned tag |
| **Pinned tag** | `v1.2.3` (2026-09-15, commit `da7b68f9`) |
| **Fork** | <https://github.com/DatMoshu/GUO-Pixelorama> (MIT, forked from upstream). `run.py fetch` clones it; `upstream` is kept as a second remote for rebasing. Nothing is pushed to it from here. |
| **Godot** | project targets **4.7** (`config/features`), Extensions API version 9, GDScript only, no GDExtension |
| **Release build** | `Pixelorama-Windows-64bit.zip`, SHA-256 `942e38288b818ef44e0c834691a00a521f811739af523a2cfdc09aae58753646`, into `bin/` (gitignored) |

Which Godot: the release build carries its own engine, so it needs nothing of
ours. The *source* opens with the pinned Godot 4.7.2 mono
(`godot-console --path tools/pixelorama/src`) because it is plain GDScript on
4.7; `run.py check` uses that to parse the extension against Pixelorama's real
classes.

## Use

```
python tools\pixelorama\run.py fetch            source (fork, pinned tag) + release build
python tools\pixelorama\run.py status
python tools\pixelorama\run.py open a.png --sidecar a.json
python tools\pixelorama\run.py extension --install
python tools\pixelorama\run.py check
```

Launchers: `launchers\art\` (`fetch_pixelorama.bat`, `pixelorama.bat`,
`pixelorama_status.bat`). Both `src/` and `bin/` are gitignored.

The World settings panel, asset inspector and Art dock can open the selected
asset directly. See [World workspace](../../docs/world_workspace.md) for the
UI round trip. Worktrees also discover a binary in the main checkout before
falling back to PATH; `UO_PIXELORAMA` takes precedence.

Extension installation patches only `GUOTools` in the `[extensions]`
section of Pixelorama's Godot ConfigFile. Multiline dictionaries and other
preferences are preserved. The first changed config is backed up alongside
it as `config.guo-backup.ini`. Regression checks:
`python -m unittest discover -s tools/pixelorama -p test_config.py`.

## The extension (`extension/`, MIT)

Pixelorama loads an extension as a resource pack: `run.py` zips
`extension/` into `src/Extensions/GUOTools/...`, copies it to Pixelorama's
user `extensions` folder and enables it in `config.ini` (a new extension starts
disabled). It adds a **GUO** menu (Project menu):

- hue palettes from the user's own `hues.mul`, read at run time from
  `hues.json`, which the GUO editor writes into the exchange folder (never
  shipped);
- templates: land diamond 44x44, static with its foot line, gump, animation
  frame with a centre point;
- a size and transparency check;
- **Save back to GUO**: the flattened image as PNG plus the sidecar into
  `<exchange>/in/`, which GUO's watcher imports into the asset overlay.

The exchange folder is `UO_ART_EXCHANGE` (default `build/art_exchange`),
passed to Pixelorama as `GUO_ART_EXCHANGE`; the open sidecar as
`GUO_ART_SIDECAR`. Nothing is ever written under `UO_CLIENT_DATA`.

## Pixelorama tab in the GUO editor (experimental, Windows)

The `guo_editor_pixelorama` plugin adds a Pixelorama tab to the GUO editor. **Edit in Pixelorama** on an
asset opens Pixelorama in its own window, as before; **Edit in the Pixelorama tab** opens the same
working copy inside the tab. Opening another asset or reloading the assembly detaches the running
session into its own window, so unsaved work is kept. One document per session; other platforms use
the separate window only.

`GUO_ART_SOURCE_PNG` binds the sidecar to the one project opened from GUO. **Save** on that project
keeps a `.pxo` under `<exchange>/projects/` and hands the flattened PNG to GUO's exchange watcher;
**GUO: save back to GUO** from any other tab is refused. Sidecar numbers are written back as integers.

Right-click an asset grid for the same actions as the inspector, plus Copy ID and Properties.

## Animation working copy

**Open working copy** on an animation opens a window that imports an image sequence or a sheet, exports
frames or a sheet with action, direction and centre metadata, previews anchored playback, and reads
and writes classic VD types 0/1/2 (palette words and index extra are kept; fork types 3/4 are
refused). **Apply to editor overlay** stores the clip as an editor overlay (see
[docs/data_formats.md](../../docs/data_formats.md)), which is how an edit reaches the game. Frame
painting goes through **Edit in Pixelorama** on the animation, and its sheet exchange.

## Checks

- `python -m pytest tools/pixelorama` - settings and path handling.
- `python tools/pixelorama/run.py check` - extension parses, and ten binding and save-routing checks.
- `python tools/pixelorama/run.py save-check` - real Pixelorama saves twice and reopens the `.pxo`.
- `dotnet run --project tools/pixelorama/AnimationChecks.csproj` - classic VD fixtures.
- Set `GUO_PIXELORAMA_SAVE_PROOF` to `build/pixelorama/native_save_exchange` for the editor smoke to
  import the pairs `save-check` produced.

Painting, undo and keyboard input inside the embedded tab have not had an artist pass yet.

### Extension API limits (for the fork)

Noted, not patched: the API has no hook to add a *live hue preview* to the
canvas, no way to add a docked panel that follows the cel size, and
no "on save" signal for a plain PNG export, so Save back is an explicit menu
item. These are candidates for the fork.
