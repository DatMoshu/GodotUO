# MapGen: provenance

`tools/mapgen` (ADR-0030) holds the MapGen procedural map generator. This file records where each part
came from and under what terms.

| Part | Origin | Terms |
|---|---|---|
| `tools/mapgen/MapGen/**`, `MapGen.Tests/**` | The owner's MapGen project, written as an addition to a fork of CentrED# (kaczy93/centredsharp). Imported 2026-10-03 from the owner's working copy, with the Felucca-calibration work up to the stage18 preset | The owner's own code; the fork is MIT (`tools/mapgen/LICENSE.CentrEDSharp`) |
| `MapGen/Compat/RectU16.cs`, `MapGen/Compat/Uop.cs` | CentrED# `Shared/Network/RectU16.cs` (minus the packet reader) and `Shared/Uop.cs` | MIT, kaczy93 |
| `MapGen/presets/*.preset.json` | The owner's presets: pass parameters only, no tile rows. `felucca-stage18` is the measured Felucca candidate; its mined atlases are referenced by path and resolve into `UO_MAPGEN_DATA` | The owner's own (every commit by the owner's account in their project) |
| `MapGen/presets/tile-tables.default.json` | The owner's tile pools: a few dozen land tile ids per biome, chosen from tiledata names and the owner's own Felucca measurements (the file and `TileTables.cs` say so) | The owner's own |
| `MapGen/Mining/DragonRulesImporter.cs`, `MapGen.Tests/DragonRulesImporterTests.cs` | The owner's MapMiner importer for Dragon's transition rules (namespace kept) | The owner's own |
| `landbrush.dragon.json` (**not in the repo**) | Built per user by `guo-mapgen prepare --dragon DIR` from the transition rule files (`Scripts/map/*.txt`) of the user's own copy of the community map tool Dragon (release 1.03.62 with the Imod13 mod), into `UO_MAPGEN_DATA`. It holds tile ids and neighbour masks only | **Unverified.** Dragon's rule files carry no licence statement, so GUO does not ship the table until the owner decides |
| UO Landscaper statics and transitions (**not in the repo**) | Copied per user by `guo-mapgen prepare --landscaper DIR` from the user's own UO Landscaper install or norad32's mod, into `UO_MAPGEN_DATA` (`landscaper-statics/`, `landscaper-transitions/`) | Not cleanly MIT: see below. GUO does not ship them |
| `data/dungeon-roster.json`, `data/dungeon-decor-frequencies.json` | Hand-written by the owner: ModernUO creature class names and about 40 dungeon static ids with weights (the files say "hand-curated") | The owner's own |

## Audit of third-party map-tool data (2026-10-03)

**Dragon (transition rules).** Its rule files carry no licence statement. GUO keeps only the owner's
importer; users build the table from their own copy (`prepare --dragon`).

**UO Landscaper.** The original tool (Dknight and Khaybel of OrBSydia) is closed and its data carries
no open licence. norad32's mod (github.com/norad32/uo-landscaper-mod, MIT, created 2024-11-22) is
checked from its full history (70 commits):
- Its second commit, `3d6064c` "Add files from UOL Release 1.4.0", imports the original release's
  data: 356 transition files and 14 statics files. The MIT licence is norad32's; nothing in the
  repository grants rights from the original authors, who are credited only in the README.
- At HEAD, `Data/Transitions/land/` (1,235 files, 308,829 rules) was added in norad32's "Rework
  <biome> transitions" commits (2024-12-03 to 2024-12-07). Only 2.3% of its rules are identical to an
  original rule; 92.8% reuse a tile set found in the original, as any correct table must, since the
  client has one set of edge tiles per pair. This is strong evidence of norad32's own re-authoring.
  But the commits call it a rework of the originals, so independence is not proven.
- The rest of `Data/Transitions` (`Wild/`, `Populated/`, `Dungeon/`) is the original's: 181 files
  byte-identical to the release and 50 modified.
- The 16 statics files GUO briefly carried (from the owner's working copy): 5 identical to the original
  release (Cave, Furrows, Goblin Forest, Goblin Forest Vyseky, Lava); 8 original files with edits
  (Forest and Grass match norad32's versions; Beach, Jungle, Sand, Snow and Swamp match no commit of
  the mod, so they were edited locally); 3 written in the owner's project (Savanna, Tundra, Wetland)
  that say they reuse rows from the original files (62–71% of rows for Savanna and Wetland).
- Of the two transition files GUO carried, `1-Grass_To_50-Moss.xml` is norad32's own (added in
  `9a9eb93`); `Wild/Swamp/Moss -- Swamp.xml` is an original file with edits.

So none of it ships. It was removed from the history GUO publishes, and the generator reads it from
`UO_MAPGEN_DATA` when a user supplies it. **Without it:**
- Swamp Surface leaves the swamp interior unchanged; with it, it reworked 7,754 cells on a 1024 map;
- trunk/canopy pairing has no fallback when there is no `tree-statics.json`: 5% fewer statics
  (145,019 to 137,545 at 1024 with seed 1234567);
- presets that use Biome Static Scatter's default catalogue scatter nothing (`felucca-stage18` uses the
  mined Felucca atlas instead).

Land, heights and the likeness score do not change. With the data prepared locally, the maps hash
exactly as before (seed 42 at 256: `a862a102`; seed 1234567 at 1024: `5c9a4ed5`).

**Other data checked.**
- Presets, `tile-tables.default.json` and the dungeon files: written by the owner (every commit in
  their project is the owner's). They hold parameters, small id lists and class names, not rows from
  another table.
- The code reads Landscaper's and Dragon's file formats but embeds none of their rows. One comment in
  `ImageImportPass` cites Dragon's `maptrans.txt` as a cross-check for the mountain tile ids, next to
  the owner's own Felucca measurement.

**Not imported:**
- `IrCommitter` (commits to a live CentrED server);
- the CentrED editor windows;
- the stamp library, the coast atlas and the art caches, all mined from client data (rule 8; they
  live in `UO_MAPGEN_DATA`);
- the analysis tools under the handoff copy.

**NuGet:** SixLabors.ImageSharp 3.1.11, used by MapGen's paint import. Six Labors Split License:
Apache 2.0 terms for open-source projects.
