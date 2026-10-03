# tools/mapgen: the UO map generator

**MapGen** makes Ultima Online maps procedurally. It builds a map from noise heights through erosion,
climate, biomes, rivers, coasts, land transitions, roads and scatter. Then it validates the result and
writes legacy MUL map files. The owner wrote it as an addition to a CentrED# fork. It now lives here
(ADR-0030). Provenance: `docs/upstream/mapgen.md`.

| Folder | What |
|---|---|
| `MapGen/` | The generator library (`GUO.MapGen`, net8.0). Namespaces stay `CentrED.MapGen.*` |
| `MapGen/presets/` | Presets (`*.preset.json`), the tile tables, and the land brush table (`landbrush.dragon.json`) |
| `MapGen.Tests/` | 221 tests: determinism, terrain invariants, coasts, transitions, stamps, the MUL writer |
| `cli/` | `guo-mapgen`, the command-line front end the editor's Map Generator tab runs |
| `data/` | Shipped data: the dungeon roster and decor table (hand-written) and norad32's UO Landscaper statics and transitions (MIT) |
| `run.py` | Builds the CLI into `build/mapgen/cli` on first use and runs it with the config's paths |

## Use

```
python tools\mapgen\run.py schema
python tools\mapgen\run.py run --out build\mapgen\runs\try1 --size 1024 --seed 42 --step-previews
python tools\mapgen\run.py export --run build\mapgen\runs\try1
dotnet test tools\mapgen\MapGen.Tests\GUO.MapGen.Tests.csproj
```

- **Commands.** `schema`, `presets`, `run` and `export`, and the run folder's files, are specified in
  `docs/data_formats.md` §24.
- **Fresh folders only.** Every run writes to a fresh folder. `export` regenerates the map and writes
  `export/map/map0.mul`, `staidx0.mul` and `statics0.mul` only when the hash matches the run. Then it
  reads every cell back.
- **Never the install.** Nothing is ever written into `UO_CLIENT_DATA`.
- **Default preset: `felucca-stage18`,** the current Felucca-calibrated candidate (measured against
  Felucca's main continent). Keep its mountain and road heights as they are.

## Data that is not here

Some passes read data mined from a user's own client files. GUO never ships it:
- the stamp library (`mined/stamps`);
- the Felucca coast atlas (`Data/map-mining/felucca-coast-atlas.json`);
- tree statics (`client/ClassicUO/Data/tree-statics.json`).

Those paths resolve into the per-user data folder, `UO_MAPGEN_DATA` (default `%LOCALAPPDATA%\GUO\mapgen`),
which the CLI receives as `MAPGEN_DATA_DIR`. Without the data, those passes warn and fall back: no
stamps, and coasts keep the brush transitions. A "Prepare generator data" step that mines it from
`UO_CLIENT_DATA` is planned (ADR-0030). The validator's reports go to the same folder.
