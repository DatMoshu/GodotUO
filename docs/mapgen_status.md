# MapGen status: public baseline, shelved

MapGen, GUO's map generator, is complete enough to make a playable map with towns on it, and this is
where its public development stops. Further work continues privately. The code here stays open: the
community is welcome to take it on from this baseline.

## What is there

| Piece | Where | What it does |
|---|---|---|
| The generator | `tools/mapgen` | Noise heights, erosion, climate, biomes, rivers, Felucca-style coasts, land transitions, mountains, roads, scatter, dungeon entrances and mines; validation; legacy MUL output (`run.py run`, `export`). The editor's Map Generator tab runs it |
| Its tables | `tools/mapgen/MapGen/presets` | GUO's own transition table, scatter table and tile tables; presets, `felucca-stage18` the default |
| Town sites | the `Town Sites` and `Town Roads` passes | Fixed or found flat sites, flattened to pads, with the generator's roads ending at their gates |
| Towns | `tools/mapgen_districts` | GUO's own towns (`town.py`): streets, a plaza, lots and one generated house per lot, laid on the pads with a street from every gate |
| Houses | `tools/multi` | The house generator (`kit.py` `house`) and the committed styles (`tools/multi/styles/default.json`): stone, plaster, log and sandstone; gable, hip and flat roofs; stairs, porches, balconies, yards |
| Proof | `mapgen_districts run.py stage`, `prove` | Writes the map and the houses into a staged data set and walks a client through every town on the private shard |

A town is deterministic from a preset and a seed, and needs no client data to plan or build. Nothing
in a town is copied or derived from another game's layouts.

## How to run it

```
python tools\mapgen_districts\run.py town  --out build\mapgen_districts\look --preset hamlet --seed 4
python tools\mapgen_districts\run.py build --out build\mapgen_districts\demo --towns 3 --town-seed 1
python tools\mapgen_districts\run.py stage --out build\mapgen_districts\demo
python tools\mapgen_districts\run.py prove --out build\mapgen_districts\demo
python tools\mapgen_districts\test_town.py
dotnet test tools\mapgen\MapGen.Tests\GUO.MapGen.Tests.csproj
```

`prove` needs the private shard set up on ports of its own
(`python tools\editor_shard\run.py setup --port N` and `bridge --bridge-port M`) and a free shard account.

## How to extend it

- **A new kind of town:** add a preset to `tools/mapgen_districts/towns.json` (its fields are in
  `docs/data_formats.md` §26). Weights pick the styles, shapes, storeys and roofs; ranges size the lots
  and houses.
- **A new look:** add a style to `tools/multi/styles/` (format in §27). Generators ask a style for
  roles ("the south-east corner of a wall"), never item ids, so a new style works everywhere at once.
- **A new street plan:** `town.layout()` draws the streets and bands. Every house faces south onto a
  street today; rotating houses (`tools/multi/orient.py` already turns and mirrors multis) would let
  them face every way.
- **Town furniture:** wells, market stalls, trees and lamp posts are not placed yet. They would go into
  the world project's statics in `town.build()`, on cells the tour does not need.
- **Interiors:** houses are empty. `tools/decorate` furnishes rooms and is the place to start.

Before a change lands: `python tools\mapgen_districts\test_town.py`, `test_compose.py`, the
`tools/multi` tests and `launchers\dev\smoke.bat`.

## Where to start, for contributors

1. Read `tools/mapgen/README.md` and `tools/mapgen_districts/README.md`.
2. Run `run.py town` with a few presets and seeds and look at `preview.png`.
3. Run `run.py build` for a whole map and look at `towns_radar_sheet.png`.
4. Pick something from *How to extend it*, or a problem you see in a preview.

## Known limits

- All houses face south.
- Hip roofs on some house shapes show small seams at the hips.
- A balcony is checked by the validator but left out of the walked tour: asked for the balcony door, the
  client's pathfinder (which aims at x and y only) may take the walker to the front door below it.
- The importers in `tools/layout_import` remain optional tools that read a user's own data at runtime;
  their output is never committed or published.
