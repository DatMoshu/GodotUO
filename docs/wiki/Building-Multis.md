# Building Multis

A **multi** is a list of components (item, x, y, z, visible) that the client
and the shard place as one object: houses, keeps, boats, castles. GUO's tools
learn how the client's own buildings are put together, generate new ones,
check them, and prove them in game on a private shard.

Nothing here writes to your UO install. New multis go into a **staged** data
set (ADR-0022, and [Author UO Data](Author-UO-Data.md)), and the client loads
it on top of the install.

## The flow

```
python tools\multi\run.py mine                  catalogue the client's multis and the buildings in a facet (about 10 s)
python tools\multi\run.py build DESC.json       expand a description, validate it, render previews
python tools\multi\run.py write NAME            write it into the stage and read it back
python tools\multi\run.py prove NAME --clip OUT.mp4   place it on the private shard, walk in the door, film it
python tools\multi\run.py show ID               render any multi, the client's or yours
```

`tools/multi/examples/` holds generic descriptions: seven houses (one and two
storeys) and a small castle scene. The description format, the catalogue and
the built multi's sidecar are documented in `docs/data_formats.md` section 16.

## What the generator can do

- **Footprints** made of several rectangles (L, T, U), with a number of storeys
  and a roof for each.
- **Extras:** porches, balconies, fenced yards with gates and paths, stairs and
  window sills.
- **Scenes** of several multis that join: walls with walkways and parapets,
  gatehouses, round towers with stairs, platforms, causeways and houses, all
  composed on one grid and cut into multis.
- **Every piece is a real client tile**, picked by role and material from the
  catalogue and tiledata, so a build reads like the era's own buildings.

## Checks

The validator checks every multi before it's written:
- ids and the z range;
- size;
- that walls close and every floor is reachable;
- that there is a door.

`walkcheck` and `prove` then walk it in game: a scripted client follows the
tour stops and reports each one it reached.

## Furnishing interiors

`tools/decorate` furnishes a generated house from what UO's own interiors hold:
1. **Mine:** furnished rooms from the facet statics, the shard's decoration
   and the client's multis, into a local SQLite database.
2. **Learn:** room kinds (kitchen, bedroom, shop and so on) and the furniture
   groups that go together.
3. **Decorate:** a seeded, repeatable furniture list for a built multi. It
   never blocks a door, stair, landing, arrival tile or window.

```
python tools\decorate\run.py mine
python tools\decorate\run.py decorate NAME --seed 7
python tools\decorate\run.py preview DESC.json
```

The database and previews come from your client data, so they stay in
`build/` and are never committed.

## Limits

- The client's z range is −128 to +127. A walkable top storey sits at z 112 or
  below.
- Tall buildings hide the player when they're south or east of a street. Put
  them to the north and west.
- Multis are a shard-side object too: to place one for real, your shard needs
  the same multi data.

## Related

- [Author UO Data](Author-UO-Data.md): the staged data set and ID ranges.
- [Dev Shard](Dev-Shard.md): the private shard the proofs run on.
- [Editor](Editor.md): view and place multis in the world editor.
- The `/uo-multi` and `/uo-decorate` agent skills run these flows in order (see
  [Working with AI Agents](Working-With-AI-Agents.md)).
