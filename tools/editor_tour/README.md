# tools/editor_tour

A scripted, repeatable tour of the real GUO editor, recorded as a video.
It walks every editor feature on screen with a caption each and checks that
each one worked.

```
python tools\editor_tour\run.py              the whole tour, live shard included
python tools\editor_tour\run.py --no-live    skip the private shard
python tools\editor_tour\run.py --no-build   skip the first dotnet build
launchers\dev\editor_tour.bat [same flags]
```

## How it works

`run.py` builds the C#, then starts the pinned editor with
`-- --guo-editor-tour <out>`. That makes `GuoEditorPlugin` add `EditorTour`
(a sibling of `EditorSmoke`, on the same machinery: the same panels, the same
`WorldView` calls, `WorldAim` shared with the smoke check). The tour:

- sizes the editor window to 3840 x 1500 (the World tab's toolbar is wide; a
  narrower window pushes the Inspector off screen). The window may be larger
  than the screen: the frames come from the editor's own viewport;
- draws a caption card, outlines on the controls being talked about, a drawn
  pointer where a scripted click lands, and a tooltip bubble with a control's
  own `TooltipText` (`TourOverlay`); a screenshot has no mouse, so these are
  drawn, not real hover;
- does each thing through the same calls as the smoke check, asserts the
  outcome, and saves a frame of the editor window with a hold time;
- writes `tour.json` (frames, holds, per-segment checks).

`run.py` then stitches the frames with ffmpeg (concat, H.264) and writes
`summary.md`: each segment, worked / skipped / failed, and what was checked.

## Segments

Intro, layout, run bar, Art, Gumps, Animations, Hues, Multis, Cliloc, Sounds,
Parity (skipped when UOWW's `uoasset` is not installed), Bulk unpack, Maps and
Show in UO World, World pick and inspector, layers and seasons, guides, placing
a multi (the server path), the world project overlay, edits with undo/redo,
world objects (items, spawners), the asset overlay (replace, revert, hue),
export and verify (and the refused export into the install), the Shard dock
live on the private shard, outro.

## Rules it keeps

- **The client install is never written.** Edits live in a world project under
  the output folder; the export goes to a scratch folder; an export into the
  install is shown being refused; the install's files' modification times are
  checked before and after.
- **No focus is taken.** The editor starts with `no_activate` and the addon
  sets the window NoFocus, as for the smoke check.
- **It never presses Start server or Start client.** They open windows on the
  desktop. It shows them, and what their tooltips say.
- **The live part uses only this checkout's private shard**
  (`tools\editor_shard`, game port 2606, editor bridge 2607; nothing else
  uses those). `run.py` sets it up if needed, starts it, and stops it
  afterwards, only if it started it. Without it (`--no-live`, or setup fails)
  the dock is shown offline and `summary.md` says so.
- **Frames are renders of client art**: `build\editor_tour\<stamp>\`, never committed.
- The windowed editor rewrites `godot/GUO/project.godot` on exit; `run.py`
  restores it.

## Output

`build\editor_tour\<stamp>\`: `editor_tour.mp4`, `summary.md`, `tour.json`,
`frames\`, `editor.log`, plus the tour's own world project and export.
The video has no audio (the Sounds segment does play the sound in the editor).

Exit codes: 0 all segments passed or were skipped with a reason, 1 a segment
failed, 2 the editor could not run.
