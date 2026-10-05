# mapgen_districts — towns on a generated map

The map generator (`tools/mapgen`) makes the land, rivers and the roads between town sites. The
layout importer (`tools/layout_import`) turns CDDA streets and lots, and Zomboid houses, into native
UO districts: land, statics, multis with doors and stairs. This tool joins them. The generator picks
the town lots and the importer's districts fill them.

```
python tools\mapgen_districts\run.py build --out build\mapgen_districts\NAME --district DIR [--district DIR ...]
python tools\mapgen_districts\run.py stage --out build\mapgen_districts\NAME
python tools\mapgen_districts\run.py prove --out build\mapgen_districts\NAME
python tools\mapgen_districts\test_compose.py
```

- **build** runs the generator twice.
  1. The first run is only for its `map.bin`. From it, `mapdump.pick_lots` finds flat lots on dry
     ground: 88x88 by default, on the 8x8 block grid, at least 180 cells apart. It takes the flattest
     first, then the ones nearest the middle.
  2. The second run passes those lots as fixed z0 `Town Sites.Sites` with streets off. Each lot
     becomes a bare flattened pad, and the generator's roads end at its four gates.
  3. The map is exported as a world project at `--origin` on facet 0. The default is 6144,2560: the
     empty east strip of Felucca, where a 1024 map fits.
  4. `compose.py` then lays each `--district` on a lot. A district is a built 72x72 layout_import
     folder (`district.json` status `native-valid`, `world/`, `parts/`, `scene.json`). It goes 8 cells
     in from the lot's edge, and a road runs from every gate across that apron into the district's
     own streets, round buildings and statics.
  5. `radar_towns.png` and one crop per town draw the map in the client's radar colours, with the
     districts' multis on top.
- **stage** is layout_import's `district-stage`. It writes the whole map and the multis into
  `stage/`, then reads both back.
- **prove** runs the private shard (`tools/editor_shard`, set up with its own ports) and a scripted
  client. The client arrives at each town on a generated road outside a gate. The staff account
  steps there by command, and the report records it as a jump. It then walks through the gate, up
  the gate's street and through the district's tour (`--town-stops`, default 55 per town, keeps it
  inside the recorder's 15 minutes).
  - Output: `proof/report.json`, `walk.mp4` and `walk_tight.mp4`.

Districts come from layout_import's own commands (`district`, `hybrid`, `combine`). Their source data,
the built folders and every output here stay in ignored `build/` folders. The source licences ride
along in `world/SOURCE-LICENSE.txt`. Nothing here ships CDDA or Zomboid data.

The data contract is in `docs/data_formats.md` §26: `pois.json` and the `built/` folder.
