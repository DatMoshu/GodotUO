# mapgen_districts — towns on a generated map

The map generator (`tools/mapgen`) makes the land, rivers and the roads between town sites. This
tool puts a town on each site. By default the towns are GUO's own (`town.py`): streets, lots and
houses built by rule from `tools/multi`'s house generator and its committed styles. Every town is
deterministic from a preset and a seed, needs no client data to plan or build, and ships with the
repository. Status and where to start: `docs/mapgen_status.md`.

```
python tools\mapgen_districts\run.py town  --out build\mapgen_districts\look --preset market_town --seed 1
python tools\mapgen_districts\run.py build --out build\mapgen_districts\NAME [--towns 3] [--town-preset P ...] [--town-seed S]
python tools\mapgen_districts\run.py stage --out build\mapgen_districts\NAME
python tools\mapgen_districts\run.py prove --out build\mapgen_districts\NAME
python tools\mapgen_districts\test_town.py
python tools\mapgen_districts\test_compose.py
```

## GUO towns (`town.py`, `towns.json`)

A town is a 72x72 district:

- **Streets.** A main cross through the middle with a paved plaza where it meets, a street along the
  south edge, and up to two side streets the seed keeps. All are three cells wide, in MapGen's own
  cobblestone; the rest is its grass.
- **Lots.** The rows above each street (or the plaza) are a band. A band is cut into lots across x,
  with a one-cell alley between them.
- **Houses.** Each lot gets one house from `tools/multi/kit.py` (`house`): style, shape (rect, L, T,
  U), width, depth, storeys, roof, rooms, yard and balcony come from the preset's weights and the
  seed. The house faces south, its front step on the street. A house that does not fit, or does not
  validate, is retried smaller and plainer; a lot where none fits stays a green. A lot the plaza bites
  gets a short paved path from the front down to the street.
- **One multi per house**, so every part stays inside the shard's multi limits, and no two parts
  overlap.
- **The tour** starts at the crossing and walks the streets to each house, in through the front,
  up every stair and back out (`fort.walk_tour` adds the stair stops). With the install there, the
  offline walk (`tools/multi/walkcheck.py`) checks it before any shard run.

`towns.json` (`guo.mapgen.towns/1`) holds the presets: `hamlet`, `market_town`, `sandstone_village`,
`stone_borough`. `plan.json` (`guo.mapgen.town/1`) is one town fully spelled out. Both are in
`docs/data_formats.md` §26.

## The map build

**build** runs the generator twice.

1. The first run is only for its `map.bin`. From it, `mapdump.pick_lots` finds flat lots on dry
   ground: 88x88 by default, on the 8x8 block grid, at least 180 cells apart. It takes the flattest
   first, then the ones nearest the middle.
2. The second run passes those lots as fixed z0 `Town Sites.Sites` with streets off. Each lot
   becomes a bare flattened pad, and the generator's roads end at its four gates. The mountains,
   roads, pads and mines come from the generator as before.
3. The map is exported as a world project at `--origin` on facet 0. The default is 6144,2560: the
   empty east strip of Felucca, where a 1024 map fits.
4. `--towns N` GUO towns are built into `towns/`, the presets taken in turn (`--town-preset` to pick
   them), town *i* seeded `town-seed * 1000 + i`.
5. `compose.py` lays each town on a lot. It goes 8 cells in from the lot's edge, and a road runs from
   every gate across that apron into the town's own streets.
6. `radar_towns.png` and one crop per town draw the map in the client's radar colours, with the
   houses on top.

**stage** writes the whole map and the houses into `stage/` and reads both back (the canonical
`tools/world` and `tools/multi` writers).

**prove** runs the private shard (`tools/editor_shard`, set up on its own ports) and a scripted
client. The client arrives at each town on a generated road outside a gate (the staff account steps
there by command; the report records it as a jump), walks through the gate, up the gate's street and
through the town's tour (`--town-stops`, default 55 per town, keeps it inside the recorder's 15
minutes). Output: `proof/report.json`, `walk.mp4` and `walk_tight.mp4`.

## Imported districts (optional)

`--district DIR` lays built `tools/layout_import` districts instead of GUO towns. Those are built
from the user's own copies of other games' data at runtime; their folders and everything made from
them stay in ignored `build/` folders and are never committed or published (read
`tools/layout_import/README.md` first).

The data contract is in `docs/data_formats.md` §26: `pois.json`, the town presets and plans, and the
`built/` folder.
