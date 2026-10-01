# ADR-0020: Asset Edits Live in the World Project as PNG, and Export as a Verdata Patch Set

## Status

Accepted

## Date

2026-09-27

## Last Verified

2026-09-27 (phase 5), on the pinned Godot 4.7.2 mono against a UOP-only
install (no `verdata.mul`):

- `python tools\editor_smoke\run.py --headless --reload` passes, both
  passes. The smoke draws its own fixtures at test time: a 44x44 land tile, a
  30x40 static with a hole, empty rows and opaque black, a 50x20 gump with a
  transparent window, and a 32-colour hue. It imports them as land 0x0244,
  static 0x0E75, gump 0x0064 and hue 33.
  - `ArtLoader.GetArt` and `GumpsLoader.GetGump` return the saved images
    with 0 pixels differing, and `HuesRange` holds 32/32 of the colours.
  - A 40x40 land tile and an 1100-wide static are refused and write nothing.
  - Revert brings back the install's static (0 pixels differing).
  - The World tab's own loaders return the imported static.
  - `tools\world` exports the project and `verify` passes. `verify` fails on a
    copy with one colour changed.
  - An export with `--out` under `UO_CLIENT_DATA` is refused and nothing is
    created there. The install's `art*`, `gump*`, `hues*` and `verdata*`
    modification times are unchanged.
- `python tools\editor_asset_roundtrip\run.py` on a project holding those
  four assets plus one map block that uses them: export and verify pass.
  - A headless GUO client, with `files_override` pointing at the export, logs
    `>> PATCHING WITH VERDATA.MUL` for FileID 4 blocks 580 and 20085 and
    FileID 12 block 100. Its `--asset-probe` decodes all four assets with 0
    pixels differing and 32/32 hue colours.
  - The control run without the override matches 0 of 4.

**Not yet verified:** a frame from a logged-in client showing the replaced
art in the world. `tools\world_parity` does that comparison but needs
windows, and tonight's runs were headless. It is the next check when a
windowed run is allowed.

## Decision Makers

Project owner (Moshu); GUO director session (uo-port-public-11); GUOEditor
session.

## Summary

Phase 5 of docs/editor_plan.md: edited art, gumps and hues. The editor never
writes the install (AGENTS.md rule 8), so, as with map blocks (ADR-0011),
replacements live in the **world project**, under `assets/`:

- **Land art, static art and gumps are PNG files**, stored already reduced to
  UO's 15-bit colour.
- **Hues are JSON**, holding the 32 colours as the 16-bit values the file
  stores.

The editor lays them over the loaders the way the client's verdata patch
layer does, by repointing `UOFileIndex` entries at a scratch file. Hues are
written into `HuesRange`. No ported file changes. Export produces a
**patch set** a client and a shard can take without touching the install:

- a `verdata.mul` for art and gumps;
- a patched copy of `hues.mul`;
- `files_override.txt`.

This is the plan's reserved ADR-0013, renumbered: 0013 was reserved on the
editor branch before the index moved on, and the director assigned 0020.

## Engine Compatibility

Godot `Image` for PNG read and write (the editor side), Pillow for the tools.
The loaders are the ported `ArtLoader`, `GumpsLoader`, `HuesLoader` and
`UOFileManager`, unchanged.

## ADR Dependencies

- ADR-0010 (the addon): all editor code is `#if TOOLS`.
- ADR-0011 (world project): the same folder, fingerprint and `tools\world`
  export. `export.json` gains an `assets` record.
- ADR-0015 (World tab): the overlay is applied to the tab's own
  `UOFileManager` too.

## Context

### What the code offers

- Every art and gump read goes through a `UOFileIndex`, and both
  `ArtLoader.LoadLand`/`LoadArt` and `GumpsLoader.GetGump` read from
  `entry.File` when it is set. Upstream's verdata pass
  (`UOFileManager.Load`) uses exactly that to patch art (FileID 4) and gumps
  (FileID 12, size in the record's extra word).
- Upstream applies a non-empty `verdata.mul` on **every** client version
  (`forceVerdata`), and `GetUOFilePath("verdata.mul")` honours
  `files_override`. So a verdata file beside an export reaches any
  ClassicUO-lineage client with no setting changed.
- Upstream's verdata pass does **not** patch hues (FileID 32 is not handled).
  A patched `hues.mul`, found through `files_override`, is the route for hues.
- The renderer caches decoded sprites (`Renderer.Arts.Art`,
  `Renderer.Gumps.Gump`) in private arrays with no invalidation call.

### Colour

- UO art is 15-bit colour. In statics and gumps, 0 is transparent.
- A 32-bit image is reduced by dropping the low three bits of each channel.
  Alpha under 128 is transparent. Opaque black would become 0 (transparent),
  so it is stored as `0x0421`, the nearest non-zero grey.
- Land has no transparency: only the 1,012 pixels of the 44x44 diamond exist,
  and 0 is black.
- The saved PNG is the reduced image, so the file shows exactly what the
  client will draw, and a re-import is lossless.

## Decision

### The model

```
<UO_WORLD_PROJECT>/assets/
  art/land/0xNNNN.png      44x44; outside the diamond ignored
  art/statics/0xNNNN.png   up to 1024x1024
  gumps/0xNNNN.png         up to 2048x2048
  hues/0xNNNN.json         hue number (1-based), name, table start/end, 32 colours
```

- Files are named by the ids shard authors use: the static's item id, not
  `0x4000 + id`.
- The format is in `docs\data_formats.md` §11.

### In the editor

- `AssetOverlay.Apply(files)`:
  - encodes every replacement in the client's own layout into
    `.cache/overlay_assets_*.bin`;
  - repoints `Arts.File.Entries[i]` / `Gumps.File.Entries[i]` at it;
  - writes hues into `HuesRange`, saving each original first.
- Disposing the result restores the originals and deletes the scratch file.
- `EditorData` applies the project's overlay at load and again after every
  import or revert. The Assets dock redraws.
- The World tab applies it to its own loaders, then clears the changed
  sprites from the renderer's caches through reflection (editor code only;
  no hook in ported code) and reloads the chunks in view.
- The UO Inspector gains **Save PNG...**, **Import PNG...** and **Revert** on
  art and gumps, and **Save strip PNG...**, **Import strip PNG...** and
  **Revert** on hues. A hue strip is one pixel per colour, and the hue's name
  and table range are kept.

### Export (`tools\world export`)

- **`verdata.mul`**: one record per replaced art or gump, merged with the
  install's `verdata.mul` if it has one. On the same FileID and block, the
  project wins.
- **`hues.mul`**: the install's, copied, with each replaced hue's 88 bytes
  written in place.
- Both are listed in `files_override.txt`, next to any map files. An export
  can carry blocks, assets or both.
- `verify` decodes the patches with `tools\guo\uoart.py`, an independent
  reader of the loaders' layouts, and compares them pixel for pixel with the
  PNGs. It also checks that every hue outside the project, and every kept
  install patch, is unchanged.

### The invariant, restated

Nothing here writes under `UO_CLIENT_DATA`:

- The overlay's scratch file lives in the project's `.cache`.
- Export refuses an `--out` inside the install, and editor_smoke checks that
  refusal on every run.
- The patch set derives from the install, so it is never committed: `*.mul`
  and `build/` are gitignored.
- Test fixtures are drawn at test time, never read from the install.

### Out of scope

- Animations, sounds, multis, texmaps, fonts and tiledata. Tiledata is the
  obvious next one: verdata FileID 30 already carries it.
- Rewriting UOP archives, and ids beyond the install's index size.
- Re-uploading the hue shader's palette in the World tab: a hue edit shows in
  the Assets dock at once, and in the World tab after it restarts.
- GUO's own `<install>\Art\Statics\*.art` layer (read by `ArtLoader` before
  the index) takes precedence over both the overlay and verdata. That is by
  design, and never written by the editor.

## Alternatives Considered

### Patched copies of the art and gump archives

Copy `artLegacyMUL.uop` / `gumpartLegacyMUL.uop` (hundreds of MB) and rewrite
entries. This is heavy for a handful of images and means writing UOP. The
verdata route is upstream's own and reaches the client with nothing changed.
Rejected.

### A hook in `ArtLoader.GetArt` that consults the project

This is one PORT DEVIATION in a hot, merged file. It would also bypass the
index that every other reader uses. The entry repointing is what verdata
already does. Rejected, as for maps in ADR-0011.

### GUO's own `Art\Statics\*.art` folder as the export

It is read from the install folder, and writing there is forbidden. It is
also GUO-only, where verdata works in any ClassicUO client. Rejected.

### Binary or indexed-colour storage in the project

PNGs open in any image editor and diff visually on the forge. The binary form
is the scratch file and the export. The source of truth stays reviewable.

## Consequences

### Positive

- No ported file changes: the whole phase is editor code, tools, and one GUO
  Bootstrap probe.
- The export works for any ClassicUO-lineage client through
  `files_override`. A shard that serves verdata can ship the same file.
- The round trip (import, view, export, read back in the client, diff) runs
  headless and needs no shard.

### Negative

- Each apply rewrites one scratch file with every replacement. That is fine
  for tens or hundreds of images; a mass import will want an append-only
  file, as noted for maps in ADR-0011.
- The renderer caches are cleared through reflection on their private
  `_spriteInfos` field. An upstream rename breaks it loudly (a logged error,
  and the World tab keeps the old art until restarted), and nothing else.

## Risks

| Risk | Mitigation |
|---|---|
| An import quietly changes colours | The PNG is stored reduced, the smoke compares loader output with it, and verify compares the export with it |
| A patch set is committed | `*.mul` and `build/` are gitignored; fixtures are generated; the smoke never writes a `.mul` outside `build/` |
| An install already ships verdata patches | Export keeps them, ours win per id, and verify checks every kept patch byte for byte |

## Performance Implications

Applying re-encodes every replacement. The four smoke fixtures take a few
milliseconds. The editor re-applies after each import or revert, never per
frame.

## Validation Criteria

`editor_smoke`'s Assets and Export lines, and
`tools\editor_asset_roundtrip`, as in Last Verified.

## GDD Requirements Addressed

None: GUO is a port. docs/editor_plan.md §5, phase 5.

## Related

docs/editor_plan.md, docs/data_formats.md §11, ADR-0011, ADR-0015,
`godot/GUO/src/Assets/UOFileManager.cs` (the verdata pass).
