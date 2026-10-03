# tools/mapgen: the UO map generator

**MapGen** makes Ultima Online maps procedurally. It builds a map from noise heights through erosion,
climate, biomes, rivers, coasts, land transitions, roads and scatter. Then it validates the result and
writes legacy MUL map files. The owner wrote it as an addition to a CentrED# fork. It now lives here
(ADR-0030). Provenance: `docs/upstream/mapgen.md`.

| Folder | What |
|---|---|
| `MapGen/` | The generator library (`GUO.MapGen`, net8.0). Namespaces stay `CentrED.MapGen.*` |
| `MapGen/presets/` | Presets (`*.preset.json`) and the tile tables |
| `MapGen/Mining/` | The owner's importer for Dragon's transition rules (`prepare`) |
| `MapGen.Tests/` | 224 tests: determinism, terrain invariants, coasts, transitions, stamps, the MUL writer, the Dragon importer. The 16 that need the brush table skip without it |
| `cli/` | `guo-mapgen`, the command-line front end the editor's Map Generator tab runs |
| `data/` | Shipped data: the dungeon roster and decor table (hand-written) and norad32's UO Landscaper statics and transitions (MIT) |
| `run.py` | Builds the CLI into `build/mapgen/cli` on first use and runs it with the config's paths |

## Use

```
python tools\mapgen\run.py prepare --dragon <your Dragon folder>
python tools\mapgen\run.py schema
python tools\mapgen\run.py run --out build\mapgen\runs\try1 --size 1024 --seed 42 --step-previews
python tools\mapgen\run.py export --run build\mapgen\runs\try1
dotnet test tools\mapgen\MapGen.Tests\GUO.MapGen.Tests.csproj
```

- **Commands.** `schema`, `presets`, `run`, `export` and `prepare`, and the run folder's files, are specified in
  `docs/data_formats.md` §24.
- **Fresh folders only.** Every run writes to a fresh folder. `export` regenerates the map and writes
  `export/map/map0.mul`, `staidx0.mul` and `statics0.mul` only when the hash matches the run. Then it
  reads every cell back.
- **Never the install.** Nothing is ever written into `UO_CLIENT_DATA`.
- **Default preset: `felucca-stage18`,** the current Felucca-calibrated candidate (measured against
  Felucca's main continent). Keep its mountain and road heights as they are.

## Data that is not here

**The land brush table** (`landbrush.dragon.json`) holds the tile transitions between biomes. It is
converted from the rule files of the community map tool Dragon, whose terms are unverified, so GUO does
not ship it. Build it once from your own Dragon copy (a Dragon folder, or its `Scripts/map`) with
`run.py prepare --dragon DIR`. It goes into `UO_MAPGEN_DATA`. Without it, biome borders stay hard
edges, and Land Transitions says so in its warnings. For tests, `MAPGEN_BRUSH_TABLE` can point at a
table anywhere.

Some passes read data mined from a user's own client files. GUO never ships it either:
- the stamp library (`mined/stamps`);
- the Felucca coast atlas (`Data/map-mining/felucca-coast-atlas.json`);
- tree statics (`client/ClassicUO/Data/tree-statics.json`).

Those paths resolve into the per-user data folder, `UO_MAPGEN_DATA` (default `%LOCALAPPDATA%\GUO\mapgen`),
which the CLI receives as `MAPGEN_DATA_DIR`. Without the data, those passes warn and fall back: no
stamps, and coasts keep the brush transitions. A "Prepare generator data" step that mines it from
`UO_CLIENT_DATA` is planned (ADR-0030). The validator's reports go to the same folder.

## The editor tab

The GUO editor's **Map Generator** tab (`godot/GUO/addons/guo_editor/MapGen`) runs this CLI through
`run.py`. It has presets, a seed, a size and six quick knobs. Each pass gets a group with an on/off
toggle, a control per setting, reset, randomise and locks. A radar, biome and height preview shows the
result, with a stepper over the per-pass pictures. A Felucca-likeness card scores it. **Export map
files** writes the MUL triad and a world project, and **Open in UO World** opens that project. The
editor smoke checks the tab (stage `mapgen`).

## Known look problems (2026-10-02)

These come from a 1024x1024 `felucca-stage18` run with seed 1234567. Each was checked against the
images after a Gemini review; tune the generator against them:

1. **Roads draw rectangles** and run in long axis-aligned stretches with right-angle turns. They
   should wind between places.
2. **Mountains are plateaus.** In `height.png` each range is one flat, bright block with no ridges or
   valleys inside it. Change only the range shape: the owner's mountain and road heights stay.
3. **Rivers run dead straight** along mountain feet for long stretches, on diagonals.
4. **Land runs off the map edge** on the left, right and bottom.
5. **No beaches:** `shore_sand` measured 0%, against 31% in Felucca. The coast atlas is mined data,
   and it was absent here. Expect this until the "Prepare generator data" step exists.
6. **Too flat:** 65% of land is at z 0, against 56% in Felucca.
