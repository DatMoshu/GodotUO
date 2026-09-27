---
name: editor-smoke
description: "Prove the GUO editor addon works: open the real Godot editor on the project, let the addon walk every UO Assets panel against the real client install, optionally rebuild and hot-reload the C#, and report per-panel results with screenshots. Use after any change under godot/GUO/addons/guo_editor, and before claiming an editor phase is done."
argument-hint: "[headless] [reload] [art <id>]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# Editor Smoke

Runs `tools/editor_smoke` and turns its report into a verdict you can put
your name to. See `tools/editor_smoke/README.md` for what the tool does and
`docs/architecture/ADR-0010-editor-addon-shape.md` for why.

## 1. Run it

Pick the mode from the arguments. With none, run `--reload` headless: that is
the full verification the `uo-editor-engineer` agent requires after any addon
change. **Never open a window unless the person at the machine has agreed**:
a Godot window takes the desktop's focus while it runs. `windowed` adds
`--windowed` (screenshots); `reload` adds `--reload`.

| Argument | Command | Proves |
|---|---|---|
| (default) | `python tools\editor_smoke\run.py` | every panel works, headless, no window |
| `windowed` | `python tools\editor_smoke\run.py --windowed` | the same, plus a screenshot per panel; takes focus |
| `reload` | add `--reload` | the addon survives a rebuild with the editor open |
| `art <id>` | add `--art <id>` | a different static for the Art panel |

The windowed mode opens an editor window for about a minute. It needs a
desktop session but no input. Never take screenshots of the desktop to
supplement it: the tool captures the editor from inside Godot.

Exit 0 is a pass, 1 a failed check, 2 the editor did not start or timed out.

## 2. Read the report

`build\editor_smoke\<mode>\report.json` has one entry per panel under
`panels`: the query, what was selected, image size and opaque pixel count,
the saved `png`, the editor `screenshot`, and `failures`. A `skipped` Parity
panel means UOWW's `uoasset` CLI is not installed; that is allowed.

Keys per panel: `query`, `selected`, `id`, `frames`, `text_chars`,
`image_size`, `opaque_pixels`, `png`, `screenshot` (null when headless),
`played` (Sounds), `skipped` (Parity), `ms`, `ok`, `failures`. Top level:
`client_data`, `load_ms`, `panels`, `ok`, `failures`, and with `--reload`
`reloaded` plus the first pass under `before_reload`.

## 3. Look at the images

A pass means the checks ran, not that the pictures are right. Open at least
the panel images you changed (`<panel>.png`) and, in windowed mode, one
editor screenshot (`editor_<panel>.png`; headless writes none). Say what you
saw against what each default query should show:

| Panel | Query | Expect |
|---|---|---|
| Art | 0x0E75 | a brown backpack, 44x32 |
| Gumps | 0x0064 | a gold plaque in a wooden frame, 143x101 |
| Anims | 0x0190 | a grey (unhued, as stored) human mid-stride; frame 0 of 10 |
| Hues | 0x0021 | a dark-to-bright red swatch; below it the last Art pick plain, hued red, and partial hue (unchanged unless the art is grey) |
| Multis | 0x0064 | a small plaster house, tiled roof, stone base and steps |
| Maps | 1496,1628 | east Britain streets, the sea and a bridge on the right; magenta marks the cell |
| Parity | 0x0E75 | reference, GUO and diff side by side; the diff all dimmed, no magenta |
| World (`world.png`) | map0 1496,1628 | east Britain drawn by the game: a large blue-roofed building at centre, town walls left, the bridge and river bottom right |
| World (`world_multi.png`) | multi 0x0064 | the same, plus a small red-roofed house just below centre |
| World (`world_overlay.png`) | block 187,203 | the house gone; an 8x8 diamond of water with three bare trees in front of the big building's stairs |

Editor screenshots are 4K; downscale before viewing, e.g.
`python -c "from PIL import Image; im=Image.open('in.png'); im.resize((im.width//2, im.height//2), Image.NEAREST).save('out.png')"`
into the scratchpad, not `build\` or the repo.

## 4. Check the tree

`git status` must not show `godot/GUO/project.godot` modified by the run;
the tool restores it. If it does, the run was interrupted: restore the file
from git, do not commit the editor's rewrite.

## 5. Report

State the mode(s) run, the per-panel result lines, the paths of the images
you looked at, and anything written but not run. These images derive from
the proprietary client data: never commit them or copy them out of `build\`.
