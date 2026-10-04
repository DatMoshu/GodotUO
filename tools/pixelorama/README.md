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

### Extension API limits (for the fork)

Noted, not patched: the API has no hook to add a *live hue preview* to the
canvas, no way to add a docked panel that follows the cel size, and
no "on save" signal for a plain PNG export, so Save back is an explicit menu
item. These are candidates for the fork.
