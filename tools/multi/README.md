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
- No component more than 17 tiles from its multi's centre (`fort.REACH`).
  ModernUO sends a multi only while its centre is within `GlobalUpdateRange + 4`
  (18 + 4 = 22) of the player, so a wider multi can be stood on before the
  shard has sent it: the client then walks the bare land under it. Do not
  raise it past 21.
- No two multis of a scene overlap. ModernUO's `StaticTileEnumerator` stops at
  the first multi whose bounds hold a point and that has no tile there; the
  multis after it in the sector are never looked at, so a floor of one multi
  inside another's bounds (a courtyard inside a ring wall's) vanishes on the
  shard and a walker drops to the land. A scene is therefore cut on a grid of
  35-tile squares (`fort.TILE`), one multi per square holding whatever stands
  in it, cut again on straight lines while it has too many components.
  `scene-build` reports any overlap as a problem.
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
player stands. After login every gump closes (the watch's `.closegumps`: the
paperdoll the shard opens), so frames and films show the world alone. Before placing, it takes down every multi of the stage that an
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
wall > causeway > platform) takes the cells it stands on from lower ones, and a
stair that runs into a higher element is reported. The whole scene is then cut
into multis by square (see the limits above); each names the elements it
holds.

A long stair can pause on landings, and a causeway can carry buttresses. A
house stair can put a rail round its opening upstairs, and a floor can mix
materials (a list, the first dominant) so a large paved area is not one tile. A
build never depends on what was built before it in the same process: the
catalogue forgets its picks at the start of each house or scene.

## Storeys on the map's buildings

`run.py storeys DESC --project DIR` takes buildings that already stand in the
map's statics and stacks storeys on them (`storeys.py`, data_formats section
16): the ground storey is kept as it is, the old roof goes, and walls, floors,
stairs and a flat roof with a parapet go on top. The output is a world project,
not multis: `tools/world/run.py export` and `verify` turn it into map files the
shard reads first and the client reads through its override list. Nothing
needs placing at boot, the doors and vendors downstairs keep their places, and
there is no multi overlap to worry about. The tour is walked offline as for a
scene.

`run.py world-prove PROJECT` walks the tour in game on the export: the private
shard reads it first and the client through its override list. UltimaLive
clients keep a copy of the map per shard name and never refresh it, so the
proof gives the shard a name of its own for the export's bytes and removes the
copy it made afterwards. `--no-roofs` turns the client's roof drawing off (it
hides pieces flagged as roof only; a flat roof of floor tiles stays).

## The offline walk

`scene-build` walks the tour offline (`walkcheck.py`) before any proof: a
rough model of UO movement (16 z of headroom, 5 z up a step, drops allowed, no
corner-cutting). A leg with no walk is a problem, and so is a leg that is a
long detour for its distance: the client's pathfinder is an A* on the straight
distance with 10,000 nodes, and it gave up on a walk sent the long way round a
wall because a culvert was too low to pass. Put a stop on the way instead.

The pathfinder also aims at a stop's x and y only: it ends at the first cell it
reaches there, at whatever z. A stop straight above the last one (the same room
a storey up) ends on the floor it started on, so the check reports a stop whose
x and y are nearer at another z; put a stop in the middle of the stair, then one
at its top. A step lands on the highest surface in reach, so a walker never
drops through a floor, nor sideways into a stair's flight.

Nothing above z 112 can be stood on: the client's pathfinder puts a ceiling at
128 over every cell and wants a walker's 16 under it (seen in game: a roof at
115 over a five-storey keep was never reached). A walkable roof deck is 112 at
most; `storeys` takes a `roof_z` for one lower than a whole storey.

Keep legs to 18 cells or less. The pathfinder searches only the map the client
has loaded round the player: a 20-cell leg along an open street answered "no
path" every time the walk came from the far side, and passed when the client
had stood at the goal before. The check reports a longer leg.

## Complete on every side

The client's own buildings often skip what it never shows: the north or west
gable end, trim and foundation on a back wall. Generated multis do not. Every
side has its walls, foundation, trim and gable fill, and a platform's bare
edges are faced even when the description turns faces off, so the data holds
the whole building (a 3D build of it, say).

## Known

- Towers turn their flights over three rows. A climber once snapped back to
  the floor below at the top of a stacked flight in a castle scene, while a
  lone tower climbed fine: most likely the overlap loss above (the scene's
  multis overlapped then), not the stacking itself.
