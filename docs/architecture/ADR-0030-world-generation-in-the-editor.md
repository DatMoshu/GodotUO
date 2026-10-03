# ADR-0030: World generation in the editor

**Status:** Proposed
**Date:** 2026-10-03
**Decision makers:** the owner (asked for "a powerful, easy to use UO world map generator with full
control over toggles" in GUO), the GUO director, GUOMulti

## Summary

The procedural map generator, **MapGen**, moves into GUO as `tools/mapgen/MapGen`. The owner wrote it
as an addition to a CentrED# fork (MIT). The editor gets a **Map Generator** tab built from the
generator's own parameter descriptions. It runs the generator as a separate process through a small
CLI, `guo-mapgen`, writes every result to a fresh folder, and leaves exporting and deploying as
separate, explicit steps. GUO ships the generator and its own tables. It never ships data mined from a
user's client files.

## Context

- MapGen is about 16,000 lines of C#. It runs passes over a GenIR: noise height, erosion, climate,
  biomes, rivers, coasts, transitions, roads, scatter and validation. It also has a fast MUL writer.
  It has 221 tests.
- Until now it lived in the owner's server project, beside a CentrED editor fork, and was copied into
  `tools/uomapgen` for a handoff. The owner does not want GUO running generator code from outside GUO.
- Its only links to the rest of CentrED are `RectU16`, the UOP name hash and a live-server commit
  path (`IrCommitter`) that GUO does not need.
- Its 38 passes describe their own UI: `[TunableDisplay(label, Tooltip)]`, `[TunableRange(min, max)]`
  and `[TunableTileSet]`, on 326 parameters. Hand-writing that panel would drift on every pass change.
- Some data the passes read is **client-derived**: the mined stamp library, the coast atlas measured
  on Felucca, tree statics and art caches. Rule 8 forbids committing game data.
- GUO is net8.0. MapGen was net10.0 but builds unchanged on net8.0.

## Decision

1. **Vendor MapGen into GUO.**
   - **Layout:**
     - `tools/mapgen/MapGen` (library `GUO.MapGen`, net8.0) and `tools/mapgen/MapGen.Tests`.
     - `RectU16` and the UOP hash are copied into `MapGen/Compat` with their MIT notices; `IrCommitter` is left out.
   - **Namespaces stay `CentrED.MapGen.*`**, so the code's history reads across.
   - **Provenance:** `docs/upstream/mapgen.md`.
   - **Repo-relative data paths** point at `tools/mapgen/...`.
2. **One CLI, `guo-mapgen`** (`tools/mapgen/cli`), with the commands `schema`, `presets`, `run` and `export`.
   - Contract: `docs/data_formats.md` §26.
   - Every front end (the editor tab, scripts, CI) uses it. The editor runs it as a **process**: a 7168×4096 map
     holds about 200 MB of fields, and a generator crash or a long run must not take the editor down.
3. **The panel is generated from `schema`.** The tab builds one group per pass (an on/off toggle, a
   control per tunable from its type and range, the label and the tooltip). It never lists parameters by hand.
4. **Fresh output, explicit export, explicit deploy.**
   - `run` refuses a folder that is not empty and refuses any folder inside the UO install.
   - `export` regenerates the map from the run's effective preset. It refuses unless the hash matches.
     It then writes the legacy MUL triad and reads it back cell by cell.
   - Opening the result as a world project and deploying to a shard (the private one, never 2593) are
     separate actions the user takes.
5. **Data that cannot ship lives per user.**
   - `RepoRootResolver` maps mined-data paths (`mined/…`, `Data/map-mining/…`, `client/ClassicUO/Data/…`)
     onto the generator data folder: `UO_MAPGEN_DATA`, passed to the generator as `MAPGEN_DATA_DIR`,
     default `%LOCALAPPDATA%\GUO\mapgen`.
   - Validation reports go there too, never into the repo.
   - Passes whose data is missing warn and fall back (for example, Reference Coast keeps the brush
     transitions). A later "Prepare generator data" step will mine that data from `UO_CLIENT_DATA`.
6. **Shipped data.** All of it is either our own or MIT:
   - presets and tile tables;
   - the hand-written dungeon roster and decor table;
   - two transition files and the statics catalogue from norad32's UO Landscaper mod (MIT);
   - **not** `landbrush.dragon.json`, our conversion of Dragon Imod13's transition rules. Dragon's rule
     files carry no licence statement (`docs/upstream/mapgen.md`). Until the owner clears it, the
     table is built per user: `guo-mapgen prepare --dragon DIR` runs the owner's importer over the
     user's own Dragon copy and writes the table into `UO_MAPGEN_DATA`. Without it, Land Transitions
     warns and leaves biome borders as hard edges, and the tests that need it skip with that reason.
7. **The default preset is `felucca-stage18`**, the current measured candidate. The owner's mountain and
   road heights stay as they are. The experimental stage20 is not promoted.

## Consequences

- GUO owns the generator. Generator work, including the Felucca tuning in the handoff, now happens in
  `tools/mapgen`, and the copy in `tools/uomapgen` becomes reference only.
- **The process boundary costs one JSON line per pass.** A run's previews are PNG files that the tab loads.
- **`export` regenerates instead of storing the IR.** That doubles the time to export, and it makes
  determinism a checked property: an export that does not match its run's hash is refused.
- **Client-derived passes stay idle** (stamps, the coast atlas) until the user prepares the data.
  Maps still generate without them.
- **New dependency: SixLabors.ImageSharp 3.1** (Six Labors Split License: Apache 2.0 terms for open-source
  use). Only MapGen's paint import uses it.

## Validation

- `dotnet test tools/mapgen/MapGen.Tests`: 221 tests pass on net8.0.
- The editor smoke stage `mapgen` checks four things:
  - `schema` lists every pass;
  - a 256×256 run with a fixed seed gives the same hash twice;
  - switching off one pass changes the hash;
  - `export` reads every cell and every static back without a mismatch.
