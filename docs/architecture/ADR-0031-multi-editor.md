# ADR-0031: The Multi Editor

**Status:** Proposed
**Date:** 2026-10-03
**Decision makers:** the owner (asked for "the most powerful multi editor in Godot"), the GUO director

## Summary

The editor gets a **Multi Editor** main-screen tab for houses, keeps and boats. It edits a list of
components `(item, x, y, z, shown)` on an isometric canvas, with story-aware visibility, a palette read
from the user's own client files, grouped undo, live validation, and a save that goes into a **staged**
data set through `tools/multi`. It never writes the install. This ADR covers phase 1.

## Context

- `tools/multi` authors multis from descriptions (data_formats §16) and writes them into a stage (§14,
  ADR-0022). Nothing lets a person place a wall by hand.
- The client already knows how a house is customised: `HouseCustomizationManager` (ported, BSD-2) has
  the story heights, the seven per-story visibility modes and a support-propagation legality grid. The
  tables it reads (`walls.txt`, `floors.txt`, `doors.txt`, `stairs.txt`, `roof.txt`, `misc.txt`,
  `teleprts.txt`, `suppinfo.txt`) sit in the user's install.
- The Multis panel already paints a multi in the client's order (`MultiPanel.ClientOrder`, which mirrors
  `Chunk.AddGameObject` priority and `CalculateDepthZ`, and is checked against the World tab by the
  smoke).

## Decision

1. **A tab, built like the Map Generator's** (ADR-0030): a second tiny plugin
   (`addons/guo_editor_multiedit`) owns the tab button; `GuoEditorPlugin` builds and tears down the view
   (`MultiEditView`) and releases it in `OnBeforeSerialize`. All code is `#if TOOLS`.
2. **The canvas is an editor control, not a second game scene.** It draws the loaders' art
   (`EditorData.ArtImage`, nearest sampling) at UO scale (44 px cells, 4 px per z) in the order
   `MultiPanel.ClientOrder` gives, which is now generic so both share one implementation. The World tab's
   `GameScene` is a singleton bound to `World`; a second one would fight it. The smoke compares the
   canvas order with the Multis panel's for a client multi.
3. **A document with grouped history.** `MultiDocument` holds the parts; every user action is one named
   transaction (`Do`), stored as a snapshot, so a history list can jump to any state and redo works after a
   jump back. Selection is by part identity and is not history.
4. **Stories are the client's:** floor n is at z `7 + 20 n` (the foundation is below 7). Each story has
   one of the client's seven vision modes (normal, transparent content, hide content, transparent floor,
   hide floor, translucent floor, hide all), with the client's own classification of floor and content.
   A translucent virtual floor is drawn at the editing z, with lower floors fainter.
5. **The palette comes from the user's install, in place.** `walls.txt`, `floors.txt`, `doors.txt`,
   `stairs.txt`, `roof.txt`, `misc.txt` and `teleprts.txt` are parsed with the ported `CustomHouse*`
   classes. All statics are listed by tiledata name. Favourites and recent tiles are kept in the editor's
   `user://` data. Nothing read from the install is stored in the repo. `housing.bin` (newer clients) is
   not read in phase 1.
6. **Validation is a pure function of the document** (`MultiValidator`), run after each change. It
   mirrors `tools/multi/validate.py` (ids with art, z range, 4,676 components, walls closed, a door) and
   adds walkable surface with headroom, double surfaces, components crossing into the next story, and a
   port of ClassicUO's `ValidateDesignGrid` (BSD-2, notice kept in the file) for the legality grid. Errors
   block a save; the rest are warnings.
7. **Saving never touches the install.**
   - The editor's own file is a *components description* (`kind: "components"`, data_formats §28) in
     `build/multi/edit/`. It is diffable text and keeps what a multi record cannot: hue.
   - Writing to a stage writes the built form `tools/multi write` already reads
     (`build/multi/built/<name>/components.json`, `multi.json`) and runs
     `python tools/multi/run.py write <name> --stage <dir>`. The writer only adds, so a changed multi under
     a used name is saved under the next free `name-N`.
   - The stage is read back by the ported UOP reader and compared component for component.
   - Afterwards the editor overlays the written multi on `MultiLoader` (a `// PORT DEVIATION (GUO):` hook
     under `#if TOOLS`), so the Multis panel and the World tab (`PlaceServerMulti`) draw it without a restart.
8. **World selection to multi.** The World tab gets an `Area` tool (two corner clicks). "Save selection as
   multi" takes the statics in that rectangle (project overlay first, else the map), centres them, keeps
   z and hue, and opens them in the Multi Editor as one undo step.
9. **A seam for generators.** `IMultiComponentSink` (implemented by the view, reached through
   `MultiEditView.Sink`) takes a named list of components and either replaces the document or adds to it
   as one undo step. `MultiEditView.AddSidePanel(title, control)` mounts another panel in the right-hand
   tabs. The generator work under `tools/multi` calls neither Godot nor this code directly.
10. **F3** gets entries: new multi, open a multi by id, save to stage, undo/redo, each tool, each vision
    mode, and "World selection to multi".

## Consequences

- Hue is an authoring and preview aid: a multi record has no hue field, so staged multis lose it.
- The canvas does not run the game's `GameScene`, so engine-only effects (light, weather) do not show.
  A later tier could render through a private scene.
- Phase 1 leaves out: copy/paste and stamps, rotate/mirror, the roof and stair smart tools, auto-walls,
  legacy import/export formats, `housing.bin`, shard deploy.

## Validation

- `dotnet build godot/GUO/GUO.csproj`.
- Editor smoke stage `multiedit`: opens a client multi, draws, erases and moves with undo and redo,
  checks that a known-bad edit raises the expected flags, saves to a temp stage and reads it back equal,
  and takes a World selection into a new multi.
- `python tools/port_drift/run.py --strict` stays green (one small hook in `MultiLoader`).
