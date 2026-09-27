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

Pick the mode from the arguments; with none, run both of the first two.

| Argument | Command | Proves |
|---|---|---|
| `headless` | `python tools\editor_smoke\run.py --headless` | every panel works, no display needed |
| (default) | `python tools\editor_smoke\run.py` | the same, plus a screenshot per panel |
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

## 3. Look at the images

A pass means the checks ran, not that the pictures are right. Open at least
the panel images you changed (`<panel>.png`) and one editor screenshot
(`editor_<panel>.png`), and say what you saw: the backpack for Art 0x0E75, a
walking human for Anims 0x0190, a house for Multis 0x0064, Britain on the
Maps radar at 1496,1628. Downscale 4K screenshots before viewing.

## 4. Check the tree

`git status` must not show `godot/GUO/project.godot` modified by the run;
the tool restores it. If it does, the run was interrupted: restore the file
from git, do not commit the editor's rewrite.

## 5. Report

State the mode(s) run, the per-panel result lines, the paths of the images
you looked at, and anything written but not run. These images derive from
the proprietary client data: never commit them or copy them out of `build\`.
