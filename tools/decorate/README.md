# tools/decorate -- furnishing generated multis from UO's own interiors

Learns how UO's buildings are furnished and uses it to furnish the interiors
of houses built by `tools/multi`. First pass: house interiors. The pieces
(rooms, kinds, templates) are not house-specific, so dungeons, fields and
outdoor areas can follow (see "Later").

```
python tools\decorate\run.py mine     [--facets 0] [--no-multis] [--decoration DIR | --no-decoration]
python tools\decorate\run.py stats
python tools\decorate\run.py decorate NAME|SIDECAR [--seed N] [--density F] [--out FILE] [--desc DESC.json]
python tools\decorate\run.py preview  DESC.json [--seed N] [--density F] [--out DIR]
python tools\decorate\run.py demo     [--seed N]        t_manor, l_townhouse, courtyard_house
python tools\decorate\test_decorate.py                  synthetic tests, no client data
```

`/uo-decorate` runs the flow. Nothing here writes into the UO install; the
database and previews are derived from client data and stay in
`build/decorate/`.

## The pieces

| File | Job |
|---|---|
| `rooms.py` | Rooms on a storey grid: floor minus walls, split at doors (known, or found as gaps in wall lines); a room's doors, windows, shape, enclosure; reachability |
| `classify.py` | An item's kind from its tiledata name (bed, table, forge, flora, ...); what counts as furnishing; a room's type from its kinds |
| `mine_decor.py` | The miner: wall clusters on a facet (plus the shard's decoration) and the client multis, storeys from floor z, rooms, furnishing, groups, templates, co-occurrence |
| `db.py` | The SQLite schema and helpers |
| `decorate.py` | The decorator: rooms of a built multi, a type per room, learned templates placed whole, keep-clear zones, path checks, a plan picture |
| `run.py` | The CLI |

## Sources

1. **The facet statics** (Felucca by default, x < 5120: no dungeons). Walls
   are clustered into buildings as `tools/multi/mine.py` does.
2. **The shard's decoration** (ModernUO `Distribution/Data/Decoration/<map>/*.cfg`):
   what the server adds at start-up. Most town furniture (bookcases, chairs,
   chests, forges, doors) is here, not in the statics. Found via
   `--decoration`, `GUO_SHARD_DECORATION`, this checkout's shard source, or
   the main checkout's (a worktree has none). Overlaid on the statics.
3. **The client multis**: mostly bare shells; they add room sizes and shapes.

## The database (`build/decorate/decor.sqlite`)

Holds item ids, counts, tiledata names and flags, and derived layouts only:
no art, no copied map or statics records. See `db.py` for the full schema.

| Table | One row per |
|---|---|
| `building` | building: source, facet, where, storeys |
| `room` | room: storey, size, area, shape, enclosed, doors, windows, type, kinds, cells |
| `opening` | door or window of a room, room-relative, and the side it is on |
| `furnishing` | placed item: id, name, kind, room-relative cell, z above floor, `against` (wall sides), `place` (corner, wall, centre, open, on-wall), near door/window, facing, the item it stands on, its group |
| `grp` | furniture group in a room (items connected cell to cell) |
| `template` | distinct group layout: items at offsets, size, the wall sides it stands against, uses, room types |
| `item` | item: how often against each side, in corners, centred, hung, stacked, near doors/windows, facing, z |
| `cooccur` | pair of kinds: rooms having both, rooms where they stand side by side |
| `room_type` | room type: count, ground/upper, area, median furnishing density, kinds |

## How it decorates

- Rooms of each storey are segmented from the sidecar's `local.storeys`
  (doors known). Stairs, their feet, landings and porch/balcony cells are left
  out of rooms.
- Kept clear in every room: a 3 x 2 block inside each door, the cell before
  each window, the 3 x 3 around a stair foot and an arrival, and the sides of
  a flight. After every placement all free cells must stay reachable from the
  doors and stairs.
- A room type is drawn per room, biggest rooms first, weighted by how often
  the type occurs on that storey, how its area fits, what the house already
  has, and whether it is a home room (bedroom, library, parlour, dining hall,
  kitchen, storage) or a work room (ground floor only, less often).
- Furniture goes in as learned templates (a bed, a table with chairs and what
  stands on it, a forge with its anvil, a stack of barrels), placed whole
  against the same wall sides as in the original, so the directional art faces
  into the room. The type's defining piece (a bed, bookcases, a forge, a loom)
  goes first; if it cannot fit, the room is tried as another type. Wall art is
  hung on plain wall cells of the right side. Groups keep a cell apart.
- Excluded: gore, rugs, garden flora, part-pieces of multi-tile furniture
  without the rest, templates wider than 6.
- Deterministic: `random.Random` seeded with `(seed, name)`.

## Later

- Dungeons, fields, outdoors: `mine_decor.facet_buildings` takes an `x_limit`
  and the decoration folders per facet; a region miner can reuse `analyse` with
  cave floors instead of wall clusters, and `classify.ROOM_RULES` takes new rows
  (camp, crypt, garden). The `flora` and `remains` kinds are already recorded.
- Rugs: recorded but not placed (they need a floor-sized layout of their own).
- Room types forced by the description (`decorate(..., types={"0:x,y": type})`
  already takes them; the description has no field for it yet).
