# ADR-0011: Map Edits Live in a World Project, as Whole Blocks Over the Install

## Status

Accepted

## Date

2026-09-27

## Last Verified

2026-09-27 (phase 3): the tools (stamp, erase, raise/lower, hue) edit through
this model, and every edit writes its block file. `editor_smoke` runs each
tool, undoes all four back to the install's block (the file is removed),
redoes one, and checks the radar repaints.

`tools\world\run.py export` turned the resulting project into patched copies
of `map0LegacyMUL.uop` / `staidx0.mul` / `statics0.mul`, and `verify` read them
back: 1 replaced block matches the project, and the other 458,751 blocks match
the install byte for byte.

A client reading that export through upstream's `files_override` drew the
exported tree exactly where the editor draws it (`tools\world_parity`: 99.84%
identical outside masks). **Not yet verified:** the dev shard reading the
export (the export folder first in its `dataDirectories`, then a restart). It
is staged, but it needs the shared shard to restart, and that has not been
permitted yet.

2026-09-27: `editor_smoke`'s overlay stage passes windowed and headless,
before and after an assembly reload. It creates a project, captures block
187,203 of map0, turns all 64 land cells to water (0x00A8) and adds three
statics (0x0CCA), and writes the block. It reopens the project from disk and
lays it over the loaded map. The chunk then holds 64 water cells and 3 of
the statics, and the frame shows them. Closing the project puts the install's
block back (0 statics). The modification times of every `map*`, `statics*`
and `staidx*` file in `UO_CLIENT_DATA` are unchanged across the run.

## Decision Makers

Project owner (Moshu); GUO-Fable (plan author); GUOEditor session.

## Summary

The editor never writes to the client install (CLAUDE.md rule 8). Map edits
are kept in a **world project**, a folder at `UO_WORLD_PROJECT`, as whole
replaced 8x8 blocks, one JSON file per block. The project is laid over a
loaded map by repointing those blocks' `IndexMap` entries at a scratch file
in the client's own block layout. This is the mechanism UltimaLive and the
verdata patch layer already use, so no ported file changes. Export to the
shard is phase 3 (`tools/world`), into the shard's data folder only.

## Engine Compatibility

Engine-independent: plain .NET file IO and the ported `MapLoader` /
`IndexMap` / `UOFileMul`.

## ADR Dependencies

- ADR-0010 (the addon), ADR-0015 (the world view it feeds).
- Enables ADR-0012 (live edits travel as blocks) and phase 3's tools and export.

## Context

### Problem Statement

An editor that edits maps must not write the install, yet what it shows must
be what the client would show with the edit applied. The client already
solves "a layer over the base files" for its own patches: `mapdifN` /
`stadifN` and verdata repoint individual blocks.

### What the code offers

- `MapLoader.BlockData[facet][block]` is an array of `IndexMap` structs, with
  public fields: `MapFile`, `MapAddress`, `StaticFile`, `StaticAddress`,
  `StaticCount`.
- `Chunk.Load` reads a block only through its entry's readers
  (`im.MapFile.Seek(...)`, `Read<MapBlock>()`, then the statics span).
- UltimaLive reloads a changed block by setting aside non-terrain objects,
  `Chunk.ClearForReload()`, `Chunk.Load(map)`, and adding them back.

## Decision

### The model

- **Whole blocks, not cell deltas.** It matches how `mapdif`/`stadif`,
  UltimaLive and the shard think. It keeps a change reviewable per block, and
  makes undo (phase 3) a ring of block snapshots.
- **One JSON file per block** (`blocks/<facet>/<bx>_<by>.json`, format in
  `docs/data_formats.md` §9). Land is eight rows of eight `ID:Z` cells and
  statics are one line each, sorted, so a diff shows exactly what changed.
- **`project.json`** records the base install's client version and a
  fingerprint (names and sizes of the map files), so a project opened
  against a different install can be noticed.

### Applying it

`WorldProject.Apply(maps, facet)` writes every block of the facet to
`.cache/overlay_map<facet>_<n>.bin` in the client's layout (header and 64
cells, then 7-byte statics), opens it with `UOFileMul`, saves each touched
entry's original the first time, and repoints the entry. Blocks the project
no longer has get their original back. `WorldHost` then reloads the loaded
chunks among the changed blocks the UltimaLive way. Unloaded chunks read the
new entry when first needed. `CloseProject` restores every original, reloads,
and deletes the scratch files.

### Editing and undo (phase 3)

- Every edit reads the block (from the project, or captured from the map
  when the project does not have it yet), changes it, writes the file, and
  re-applies the overlay. The file is the save; there is no unsaved state.
- Undo is the block ring buffer the plan proposed: each entry holds the
  block file's text before and after, with null meaning "the install's
  block". Undo writes the before text (or removes the file), redo writes the
  after text. The last 200 edits are kept. Opening another project clears
  the history.
- It felt right with the altitude tool: one click is one entry, and undo
  puts the cell back exactly, neighbouring corners included, because the
  whole block is restored.

### Export (phase 3)

`tools/world` copies the touched facets' land, index and statics files and
patches only the replaced blocks. Land cells are written in place at
`MapLoader`'s offsets, keeping each block header. Statics are appended and
their index entries repointed. It writes `files_override.txt` for the client
and `export.json` as the record. An output inside `UO_CLIENT_DATA`, or a
project from another install (fingerprint mismatch), is refused. `mapdif`/
`stadif` output is not produced: nothing in the pipeline needs it yet.

### Key Interfaces

`WorldProject.OpenOrCreate(root, clientData, version)`, `Blocks(facet)`,
`ReadBlock(path)`, `WriteBlock(block)`, `Capture(maps, facet, bx, by)`,
`Apply(maps, facet)`, `Restore(maps, facet)`; `WorldHost.OpenProject`,
`ApplyOverlay`, `CloseProject`. Config: `UO_WORLD_PROJECT` in
`launchers\_shared\config.bat` and `tools/guo/config.py` (`world_project`),
default `build\world\default`.

### The invariant, restated

Nothing in this design has a path that writes under `UO_CLIENT_DATA`. The
project reads the install only to fingerprint it and, through the loaders,
to capture a block. Scratch files live in the project's `.cache`. Export
(phase 3) writes the shard's data folder, never the install.

## Alternatives Considered

### Alternative 1: Cell deltas

Store "cell (x, y) became id/z" and "static added/removed". Smaller files,
but a delta against a moving base is ambiguous: which static is "the second
0x0CCA at z 10"? Nothing downstream (mapdif, UltimaLive, the shard) speaks
deltas. Rejected.

### Alternative 2: A binary project format

Compact and fast, but unreviewable in a diff, which the plan asks for (CI
shows changed blocks). The scratch file is binary; the source of truth is
text.

### Alternative 3: A hook at the top of `Chunk.Load`

Consult the overlay directly in `Chunk.Load`. That is one PORT DEVIATION in a
hot, frequently merged file, and it would bypass the `MapLoader` state every
other reader (radar, minimap, UltimaLive) consults. Repointing entries keeps
all readers consistent. Rejected.

## Consequences

### Positive

- No ported file changes for terrain; the overlay is invisible to the
  renderer, which just reads blocks.
- Every reader of `BlockData` (the World view, and the Maps panel once it
  reads the world's loader) sees the same edited world.
- Blocks convert one to one to mapdif/stadif records for export.

### Negative

- Applying rewrites the facet's scratch file each time: fine for tens or
  hundreds of blocks; a facet-wide edit (thousands of blocks) will want an
  append-only scratch file.
- ~~The Maps radar does not show the overlay.~~ Since phase 3 it reads the
  world's loader while the world runs and repaints edited blocks.
- `World.MapIndex` changes re-apply the overlay; a full `LoadMap` of a facet
  already overlaid would rebuild `BlockData` and drop the repointing (the
  editor never does that today).

## Risks

| Risk | Mitigation |
|---|---|
| A scratch file stays mapped and cannot be deleted | Each apply uses a new file name; old readers are disposed, then deleted |
| A project made on another install is applied blindly | `base.fingerprint` is recorded; phase 3's tools check it before export |

## Performance Implications

Applying one block and reloading its chunk is immediate. Project files are
read in full on each apply.

## Migration Plan

None; new.

## Validation Criteria

`editor_smoke`'s overlay stage, as in Last Verified.

## GDD Requirements Addressed

None: GUO is a port. docs/editor_plan.md §2 and §4.3.

## Related

docs/editor_plan.md, docs/data_formats.md §9, ADR-0010, ADR-0015,
`godot/GUO/src/Game/UltimaLive.cs` (the reload pattern).
