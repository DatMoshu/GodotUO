# MapGen: provenance

`tools/mapgen` (ADR-0030) holds the MapGen procedural map generator. This file records where each part
came from and under what terms.

| Part | Origin | Terms |
|---|---|---|
| `tools/mapgen/MapGen/**`, `MapGen.Tests/**` | The owner's MapGen project, written as an addition to a fork of CentrED# (kaczy93/centredsharp). Imported 2026-10-03 from the owner's working copy, with the Felucca-calibration work up to the stage18 preset | The owner's own code; the fork is MIT (`tools/mapgen/LICENSE.CentrEDSharp`) |
| `MapGen/Compat/RectU16.cs`, `MapGen/Compat/Uop.cs` | CentrED# `Shared/Network/RectU16.cs` (minus the packet reader) and `Shared/Uop.cs` | MIT, kaczy93 |
| `MapGen/presets/*.preset.json` | The owner's presets: pass parameters only, no tile rows. `felucca-stage18` is the measured Felucca candidate; its mined atlases are referenced by path and resolve into `UO_MAPGEN_DATA` | The owner's own (every commit by the owner's account in their project) |
| `MapGen/presets/tile-tables.default.json` | The owner's tile pools: a few dozen land tile ids per biome, chosen from tiledata names and the owner's own Felucca measurements (the file and `TileTables.cs` say so) | The owner's own |
| `MapGen/presets/transitions.guo.json`, `MapGen/Data/GuoTransitionTable.cs`, `MapGen/Data/EdgeShapes.cs`, `cli/Measure.cs`, `cli/Coverage.cs` | GUO's own transition table and its tools, written in GUO on 2026-10-03 (method below) | GUO's own |
| `MapGen/presets/scatter.guo.json`, `MapGen/Data/GuoScatterTable.cs` | GUO's own scatter table and loader, written in GUO on 2026-10-03 (method below) | GUO's own |
| `transitions.guo.resolved.json`, `map-mining/guo-transition-*.json` (**not in the repo**) | Written per user by `guo-mapgen prepare --measure` from the user's own Felucca, into `UO_MAPGEN_DATA` | Measurements of client data (rule 8): never committed |
| `MapGen/Mining/DragonRulesImporter.cs`, `MapGen.Tests/DragonRulesImporterTests.cs` | The owner's MapMiner importer for Dragon's transition rules (namespace kept) | The owner's own |
| `landbrush.dragon.json` (**not in the repo**) | Built per user by `guo-mapgen prepare --dragon DIR` from the transition rule files (`Scripts/map/*.txt`) of the user's own copy of the community map tool Dragon (release 1.03.62 with the Imod13 mod), into `UO_MAPGEN_DATA`. It holds tile ids and neighbour masks only. Used only on request (`run --brushes dragon`) and as a coverage reference (`coverage`, counts only) | **Unverified.** Dragon's rule files carry no licence statement, so GUO does not ship the table |
| UO Landscaper statics and transitions (**not in the repo**) | Copied per user by `guo-mapgen prepare --landscaper DIR` from the user's own UO Landscaper install or norad32's mod, into `UO_MAPGEN_DATA` (`landscaper-statics/`, `landscaper-transitions/`) | Not cleanly MIT: see below. GUO does not ship them |
| `data/dungeon-roster.json`, `data/dungeon-decor-frequencies.json` | Hand-written by the owner: ModernUO creature class names and about 40 dungeon static ids with weights (the files say "hand-curated") | The owner's own |

## GUO's transition table (2026-10-03)

`transitions.guo.json` replaces Dragon's table as the generator's default. No row was copied from
Dragon, UO Landscaper or any other map tool, and neither was opened while the ids were chosen; the
local Dragon import was used afterwards only to check the direction convention (four pairs, matching)
and for the `coverage` counts.

What is authored: the pair list and which side owns each edge (the generator's own), the twelve
edge-shape model (`EdgeShapes.cs`), the bridges (`via`), the height offsets, the weights and the notes.

How the tile ids were chosen, per pair:
1. `prepare --measure` counted, on the owner's own Felucca, which tile sits in each edge shape between
   two plain materials (291,700 samples over 25 pairs). These counts stay on the owner's machine.
2. A contact sheet of the most used tile per shape, drawn from the owner's own client art, was checked
   by eye.
3. A second check read each tile's art directly: the diamond is split into eight direction sectors and
   each sector is classed as owner or other by colour. For high-contrast pairs (sand, rock, dirt, snow)
   it agrees with the measurement on all 12 shapes of every family (11 of 12 for dirt/cobble). It cannot
   separate green from green (grass/forest, grass/jungle, grass/swamp), so those rest on the measured
   shares (70–94% per shape) and the contact sheet.
4. Rare families (sand/rock, sand/jungle, snow/dirt) were read from the art first and agree with the
   few samples Felucca has.

Heights: the `z` offsets are rounded from the measured mean height of edge cells relative to each side.
A rock edge cell sits about 8 above the grass on north- and west-facing edges, and about 13 at the
north-west outer corner, while south and east edges barely move. Applying them is off by default
(*Edge z offsets*), so terrain heights stay the terrain passes' own.

**Moss and bog (`Swamp>Bog`).** Swamp Surface splits each swamp into a moss band and an open bog
interior. The pair's ids were read from the owner's Felucca: for each tile of the moss/bog family, which
side of it holds bog interior, then checked on a contact sheet of the art. Straight edges and outer
corners are 0x3DDB–0x3DE8. Felucca draws three of the four inner corners with ordinary moss tiles
(0x3DED, 0x3DEE, 0x3DEF: about 900 corners each) and the south-west one with 0x3DC2. Swamp Surface draws
grass against moss from the existing `Grassland>Swamp` pair.

**Shallows (2026-10-03).** Felucca's shallows are a seabed of land tiles 10 below the water statics
(z −15 under statics at −5), up to about ten cells out from the shore, then open water tiles at −5.
The seabed comes in three bands: a light ring (0x4C–0x57) next to the shore, a mid ring (0x58–0x63)
and the flat bed (0x64). The rings' twelve tiles each are edge shapes, read from the owner's Felucca:
for each tile, which side holds the deeper bed (light ring) or the shore (mid ring). On the dry side,
rippled wet sand (0x1A with the water north or east, 0x1B south or west, 0x1C around) lines the
waterline. Dig Shore digs the band; the Shallows pass gives it these shapes. The counts stay on the
owner's machine.

## GUO's scatter table (2026-10-03)

`scatter.guo.json` replaces UO Landscaper's statics as Biome Static Scatter's default catalogue and
as the trunk/canopy pairing when there is no `tree-statics.json`. No row was copied from UO Landscaper
or any other tool, and no Landscaper file was opened while it was written.

How it was made:
1. A local count over the owner's own Felucca: for every plain-ground cell of eight materials, which
   statics stand on it and in what stacks (4.9 million cells). The counts stay on the owner's machine.
2. Natural statics only, picked by tiledata name and then checked on contact sheets of the owner's
   client art: grasses, flowers, ferns, rocks, mushrooms, logs, brambles, jungle plants, cacti and
   swamp plants. Man-made statics that stand on plain ground in Felucca (docks, walls, roofs, hay,
   hedges) are left out.
3. Groups are named by what they are; each group's weight follows its share of the measured
   placements, rounded. A biome's chance is the share of its cells that hold ground cover only (no tree,
   no building), rounded: grass 5%, forest 22%, jungle 20%, swamp 8%, beach 4%. Felucca leaves snow and
   mountain ground bare, so those chances are 0 (the groups are there for anyone who raises them).
4. The tree pairs are the trunk/canopy stacks the count found on forest and swamp ground (12 pairs),
   plus the two cypresses the art sheet shows with the same layout. Each trunk takes its green canopy.

Desert is authored, not measured (Felucca's sand is mostly shore): the beach's desert plants, with
cacti first.

## Audit of third-party map-tool data (2026-10-03)

**Dragon (transition rules).** Its rule files carry no licence statement. GUO keeps only the owner's
importer; users build the table from their own copy (`prepare --dragon`) and use it only on request
(`run --brushes dragon`). With it, the maps hash exactly as before GUO's table existed (seed 42 at 256:
`a862a102`; seed 1234567 at 1024: `5c9a4ed5`).

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
- Swamp Surface draws its moss and bog edges from GUO's transition table (`Swamp>Bog`, above). It
  repaints as many cells as with Landscaper's transitions (7,243 at 1024 with seed 1234567), and the
  edges read alike in a close-up;
- trunk/canopy pairing and Biome Static Scatter use GUO's scatter table instead (above). With the
  scatter pass off, the maps hash as with the Landscaper pairing, so the pairing matches.

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
