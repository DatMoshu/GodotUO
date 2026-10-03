# MapGen: provenance

`tools/mapgen` (ADR-0030) holds the MapGen procedural map generator. This file records where each part
came from and under what terms.

| Part | Origin | Terms |
|---|---|---|
| `tools/mapgen/MapGen/**`, `MapGen.Tests/**` | The owner's MapGen project, written as an addition to a fork of CentrED# (kaczy93/centredsharp). Imported 2026-10-03 from the owner's working copy, with the Felucca-calibration work up to the stage18 preset | The owner's own code; the fork is MIT (`tools/mapgen/LICENSE.CentrEDSharp`) |
| `MapGen/Compat/RectU16.cs`, `MapGen/Compat/Uop.cs` | CentrED# `Shared/Network/RectU16.cs` (minus the packet reader) and `Shared/Uop.cs` | MIT, kaczy93 |
| `MapGen/presets/*.preset.json`, `tile-tables.default.json` | The owner's presets and tables. `felucca-stage18` is the measured Felucca candidate | The owner's own |
| `MapGen/presets/landbrush.dragon.json` | Converted by the owner's importer from the transition rule files (`Scripts/map/*.txt`) of the community map tool Dragon, release 1.03.62 with the Imod13 mod. It holds tile ids and neighbour masks only | **Unverified.** Dragon's rule files carry no licence statement. The owner decides before this file goes public |
| `data/landscaper-statics/**`, `data/landscaper-transitions/**` | norad32's UO Landscaper mod (`Data/Statics`, plus two files from `Data/Transitions`) | MIT, norad32 (`LICENSE` beside the data) |
| `data/dungeon-roster.json`, `data/dungeon-decor-frequencies.json` | Hand-written by the owner (the files say so) | The owner's own |

**Not imported:**
- `IrCommitter` (commits to a live CentrED server);
- the CentrED editor windows;
- the stamp library, the coast atlas and the art caches, all mined from client data (rule 8; they
  live in `UO_MAPGEN_DATA`);
- the analysis tools under the handoff copy.

**NuGet:** SixLabors.ImageSharp 3.1.11, used by MapGen's paint import. Six Labors Split License:
Apache 2.0 terms for open-source projects.
