# Gump Studio smoke

Run `python tools/gump_studio_smoke/run.py --reload` from the checkout root.
The tool builds the project and opens the configured Godot console executable
in headless editor mode. No shard or external account is required.

The editor tests the real authoring/runtime types: classic layout preservation,
JSON validation, history, cross-page form replies, radio groups, anchors,
bindings, scene export and reload, captured control geometry, refusal of
mismatched capture paths, workspace construction, and a snapped canvas drag.
With `--reload`, the tool rebuilds once and verifies the checks again after
Godot unloads/reloads the assembly. A timeout or failed check returns nonzero.

Evidence is local in `build/gump_studio_smoke/report.json` and `editor.log`.
With desktop-owner agreement, `--windowed --reload` also captures the workspace
at 1600×1000 and 1280×900 before and after assembly reload. The checks include
the nine supported client previews, name/ID art search, and pane visibility.
The exported fixture scene and round-trip document are saved there too.
`project.godot` is restored after the run. Client artwork is never copied to
the source tree. This is an interaction/model check, not a visual-parity test
or a live-server round trip.

The full editor suite also includes these checks:
`python tools/editor_smoke/run.py --headless --reload`.
