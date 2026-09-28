# tools/multi -- authoring new multis (houses to castles)

A multi is a list of components (item, x, y, z, shown) that the client and the
shard place as one object. This tool learns how the client's own buildings are
put together, expands a readable description into components, checks them,
writes them into a **staged** data set (ADR-0022; never the install) and proves
them in game on the private shard. `/uo-multi` runs the flow in order.

```
python tools\multi\run.py mine    [--out DIR] [--facet 0] [--no-statics]    the catalogue (about 10 s)
python tools\multi\run.py sheets  [--catalogue DIR]                         contact sheets of it
python tools\multi\run.py build   DESC.json [--out DIR]                     generate, validate, preview
python tools\multi\run.py write   NAME [--stage DIR]                        into the stage, read back
python tools\multi\run.py prove   NAME [--clip OUT.mp4] [--visit X,Y,Z]     place it, walk in, film it
python tools\multi\run.py show    ID [--data DIR] [--png FILE]              render any multi
python tools\multi\run.py scene-build SCENE.json [--cut Z]                  a scene of many multis
python tools\multi\run.py scene-write NAME [--stage DIR]                    every part into the stage
python tools\multi\run.py scene-prove NAME [--clip OUT.mp4] [--at X Y Z]    place them all, walk the tour
python tools\multi\test_multi.py                                            synthetic tests, no client data
```

## The pieces

| File | Job |
|---|---|
| `multifile.py` | Multi records: read every multi (MultiCollection.uop, else multi.mul/idx), encode new ones |
| `pieces.py` | Roles and materials from tiledata flags and names |
| `mine.py` | The catalogue: every client multi and the buildings in a facet's statics |
| `generate.py` | A description into components, every piece looked up in the catalogue: footprints of several rects (L, T, U), per-rect storeys and roofs, porches, balconies, fenced yards with gates and paths, stairs, sills, walk stops |
| `fort.py` | Scenes: walls with walkways, parapets, gates and culverts; round towers with stairs; platforms; causeways; stairs; houses; composed on one grid and cut into multis that join |
| `validate.py` | Ids, z range, size, walls closed, floors reachable, a door |
| `render.py` | Offline isometric previews, per-storey plans, contact sheets |
| `prove.py` | The private shard, a client, the walk through the door, frames and a clip |
| `examples/` | Generic descriptions: seven houses (one and two storeys) and a small castle scene |

`docs/data_formats.md` section 16 is the contract: the catalogue files, the
description format, the built multi and its sidecar.

## What the data says (measured on a 7.0.107 install)

- 871 multis, ids 0x0000-0x2328; 636 buildings in the Felucca statics.
- A ground floor stands at z 7 on a 5-high foundation at z 0; storeys are 20 z
  apart; walls are 19 or 20 high; stairs rise 5 z a step; entrance steps are
  at z 2; a gable roof starts at the wall top and rises 3 z a course.
- Doors, signs and the centre are hidden components that the shard fills with
  real items (ModernUO's BaseHouse adds the doors), so a door cell in the
  walls is a gap.
- Which piece stands at a corner, a wall end or a T depends on its neighbours:
  the catalogue records the neighbour signature of every wall, floor and
  stair piece, and the generator looks pieces up by it.

## The limits it keeps

- Multi ids stay below 0x4000 (the shard masks to 0x3FFF). The multi pack's
  range is 0x3F00-0x3FFF (`tools/uodata_write/ranges.json`).
- At most 4,676 components per multi: ModernUO reads an entry into 64 KB. A
  bigger building is several multis.
- The catalogue is derived from client data: it stays in `build/`, never in a
  commit. So do built multis, stages, proofs and clips.

## Proving on the shard

`prove` uses this checkout's private ModernUO (`tools/editor_shard`), which
must be set up on a port of its own (`setup --port N`, `bridge --bridge-port M`),
never the shared shard. It needs 16 GB free (`--min-free-gb`), starts the shard
with the stage first in its data directories, places the multi with the bridge's
`multi` op, logs a client in (windowed, never focused, with `auto_open_doors`),
and walks it with the client's pathfinder (the watch's `.goto`) through the
stops the generator wrote: through the yard's gate, onto the step, in, to the
middle of the ground floor, up each stair, out onto a balcony, then each
`--visit`. A stop counts only when the client stands at its x and y and within
4 of its z. Every stop is a frame and a dump of where the client says the
player stands. Before placing, it takes down every multi of the stage that an
earlier proof left standing (the shard keeps its world).

A scene (`scene-prove`) is placed part by part at the site plus each part's
centre, so the parts meet exactly, then its `tour` is walked. A scene larger
than the flat, clear ground near the start needs `--at X Y Z`.

## Houses and scenes

A house description (section 16) gives a footprint as one `size` or several
`rects`, each with its own storey count and roof, and per storey its openings,
partitions and stairs. Porches, balconies, a fenced yard and decor are optional.
Building twice gives the same bytes.

A scene is elements on one grid. Where a wall's walkway reaches a tower at one
of its levels the tower opens there; a higher-ranked element (house > tower >
wall > causeway > platform) takes the cells it stands on from lower ones. The
whole scene is cut into parts, one per element's `part`, and a part over the
size limit is cut again in two until each fits one multi.

## Known

- Avoid flights stacked in the same cells. A climber who reached the top of
  a flight standing over another was snapped back to the floor below (a
  resync) in a castle scene, while a lone tower with the same stacking
  climbed fine. The cause is not yet isolated (ModernUO or the client);
  towers now turn their flights over three rows so none stacks.
