# tools/mapgen: the UO map generator

**MapGen** makes Ultima Online maps procedurally. It builds a map from noise heights through erosion,
climate, biomes, rivers, coasts, land transitions, roads and scatter. Then it validates the result and
writes legacy MUL map files. The owner wrote it as an addition to a CentrED# fork. It now lives here
(ADR-0030). Provenance: `docs/upstream/mapgen.md`.

| Folder | What |
|---|---|
| `MapGen/` | The generator library (`GUO.MapGen`, net8.0). Namespaces stay `CentrED.MapGen.*` |
| `MapGen/presets/` | Presets (`*.preset.json`), the tile tables, GUO's transition table (`transitions.guo.json`) and scatter table (`scatter.guo.json`) |
| `MapGen/Mining/` | The owner's importer for Dragon's transition rules (`prepare --dragon`, optional) |
| `MapGen.Tests/` | 264 tests: determinism, terrain invariants, coasts, the transition and scatter tables, stamps, the MUL writer, the Dragon importer. None needs local data |
| `cli/` | `guo-mapgen`, the command-line front end the editor's Map Generator tab runs |
| `data/` | Shipped data: the dungeon roster and decor table (hand-written by the owner) |
| `run.py` | Builds the CLI into `build/mapgen/cli` on first use and runs it with the config's paths |

## Use

```
python tools\mapgen\run.py prepare --measure
python tools\mapgen\run.py prepare --landscaper <your UO Landscaper folder>
python tools\mapgen\run.py schema
python tools\mapgen\run.py run --out build\mapgen\runs\try1 --size 1024 --seed 42 --step-previews
python tools\mapgen\run.py export --run build\mapgen\runs\try1
dotnet test tools\mapgen\MapGen.Tests\GUO.MapGen.Tests.csproj
```

- **Commands.** `schema`, `presets`, `run`, `export`, `prepare` and `coverage`, the run folder's files and
  the transition table are specified in `docs/data_formats.md` §26.
- **Fresh folders only.** Every run writes to a fresh folder. `export` regenerates the map and writes
  `export/map/map0.mul`, `staidx0.mul` and `statics0.mul` only when the hash matches the run. Then it
  reads every cell back.
- **Never the install.** Nothing is ever written into `UO_CLIENT_DATA`.
- **Default preset: `felucca-stage18`,** the current Felucca-calibrated candidate (measured against
  Felucca's main continent). Keep its mountain and road heights as they are.

## The transition table

**`MapGen/presets/transitions.guo.json`** is GUO's own table of tile transitions between biomes, and
the default. It holds what we author: which side owns each edge, a core tile family per pair
(14 pairs, all 12 edge shapes each), bridges where Britannia puts a material between two others (sand
between grass and water, grass between forest and sand), rock-lip heights and notes. How its ids were
chosen is in `docs/upstream/mapgen.md`.

`run.py prepare --measure` measures your own Felucca and writes a resolved copy with the variants your
client uses into `UO_MAPGEN_DATA`; the generator prefers it. `run.py coverage` compares the tables you
have, in counts.

## The scatter table

**`MapGen/presets/scatter.guo.json`** is GUO's own table of what grows on each biome's ground (grass,
flowers, ferns, rocks, mushrooms, logs, jungle plants, cacti, swamp plants) and which tree trunk takes
which canopy. Biome Static Scatter reads it by default (*Catalogue* `guo`); Forest Scatter pairs
trunks with it when there is no `tree-statics.json`. How it was made is in `docs/upstream/mapgen.md`.

## Data that is not here

**Dragon's transition rules** (`landbrush.dragon.json`) are optional. GUO does not ship them (their
terms are unverified). `run.py prepare --dragon DIR` converts your own copy into `UO_MAPGEN_DATA`;
`run --brushes dragon` then uses it, and `coverage` compares against it.

The editor's MapGen tab picks the table under *Transition table*: GUO (the default, with your measured
weights when `prepare --measure` wrote them), GUO core (the committed table alone), Dragon once prepared,
or a table file of your own. It passes the choice as `--brushes`; Export reuses it.

**UO Landscaper's statics and transitions** are optional. Biome Static Scatter reads them when its
*Catalogue* names `mined/landscaper-statics`, and Swamp Surface when its *Transition catalogue* names
`mined/landscaper-transitions`; by default both use GUO's own tables. norad32's MIT mod began by
importing the closed original's data, and most of these files trace to it (`docs/upstream/mapgen.md`),
so GUO does not ship them. Copy them from your own copy with `run.py prepare --landscaper DIR`.

Some passes read data mined from a user's own client files. GUO never ships it either:
- the stamp library (`mined/stamps`);
- the Felucca coast atlas (`Data/map-mining/felucca-coast-atlas.json`);
- tree statics (`client/ClassicUO/Data/tree-statics.json`).

Those paths resolve into the per-user data folder, `UO_MAPGEN_DATA` (default `%LOCALAPPDATA%\GUO\mapgen`),
which the CLI receives as `MAPGEN_DATA_DIR`. Without the data, those passes warn and fall back: no
stamps, and coasts keep the table's transitions. A "Prepare generator data" step that mines it from
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
5. **Beaches (fixed 2026-10-03):** the card counted Felucca's seabed as land, so its 31% coast sand
   measured the seabed. Counted as water, Felucca's dry coast is 83% sand and the generator's 91%. The
   Shallows pass now shapes the seabed as Felucca does and draws its waterline (see 7).
6. **Too flat:** 66% of land is at z 0, against 63% in Felucca (counting the seabed as water).
7. **Too much sand (fixed 2026-10-03):** sand was 2.8% of land against Felucca's 1.3%. Over half of
   it lined the rivers: the grass-to-water bridge put a sand cell on every river bank, and Felucca
   draws almost no rivers in land tiles and no sand along them. `Land Transitions.RiverBankDirt` (on
   in felucca-stage18) makes those banks dirt, edged into the grass as roads are; the desert threshold
   drops from 6 to 4, leaving desert at about 0.5% of land, as Felucca's deep-inland sand is. Sand is
   now 1.25%. The waterline (also 2026-10-03): Felucca's grass coast is one cell of grass-fringed wet
   sand with plain grass behind it. The generator drew a sand cell, rippled, with a grass edge behind
   it; the Shallows pass now draws Felucca's cell and turns the grass behind plain. The analyzer's
   sand share went 0.049 -> 0.038, against Felucca's 0.034.
