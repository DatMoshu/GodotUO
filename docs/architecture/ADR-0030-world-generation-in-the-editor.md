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
6. **Shipped data is only the owner's own:** presets, tile tables, and the hand-written dungeon roster
   and decor table. No third-party map-tool data ships (audit in `docs/upstream/mapgen.md`):
   - **not** `landbrush.dragon.json`, our conversion of Dragon Imod13's transition rules. Dragon's rule
     files carry no licence statement;
   - **not** UO Landscaper's statics or transitions. norad32's MIT mod began by importing the closed
     original's data, and most of the files GUO used trace to it.

   Users may supply both from their own copies: `guo-mapgen prepare --dragon DIR --landscaper DIR`
   writes them into `UO_MAPGEN_DATA`. Without Landscaper's, Swamp Surface skips.
   - **GUO's own transition table** (`transitions.guo.json`, data_formats §26) is the default: the pairs,
     a core tile family per pair, bridges, heights and notes, all authored in GUO. `prepare --measure`
     resolves it against the user's own Felucca into `UO_MAPGEN_DATA`. Dragon's table is used only on
     request (`run --brushes dragon`). The tests use the committed table and never skip.
   - **GUO's own scatter table** (`scatter.guo.json`, data_formats §26) is Biome Static Scatter's default
     catalogue and the trunk/canopy pairing: natural ground cover per biome and the tree pairs, authored
     in GUO with chances from the owner's own Felucca, measured locally.
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
