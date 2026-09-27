# ADR-0014: Shard World Objects: a Neutral Model in the World Project, Native Files per Backend, and a Sync Step on the Shard

## Status

Proposed

## Date

2026-09-27

## Last Verified

Not yet: this ADR is written before phase 6's first slice (sprint story S5).
The slice's evidence will be recorded here when it lands.

## Decision Makers

Project owner (Moshu); GUO director session; GUOEditor session.

## Summary

Phase 6 of docs/editor_plan.md adds world objects to the editor: spawners
(and so vendors) and decoration.

- **Neutral model.** The world project holds them in a neutral model,
  `shard/objects.json`. Each object has a stable GUID, and the model knows
  nothing about any server's formats.
- **Adapters.** A backend adapter per server (`tools/world/backends/`,
  ModernUO first) writes that model as the server's **own** files at export.
- **The sync step.** The files alone do not change a running world, because
  every RunUO-lineage server, ModernUO included, keeps placed objects in its
  binary save. So each backend also has a **sync** step, run on the shard.
  It applies additions, changes, moves and deletions, keyed by GUID. On the
  private instance, the GUO bridge assembly runs the sync at boot. On any
  other shard, a GM runs the server's own import commands, which can add and
  update but not delete.
- **What is never done.** Binary saves are never parsed or written.

## Engine Compatibility

- Editor side: Godot 4.7.2 mono, the World tab (ADR-0015), and the embedded
  world's server-object path.
- Shard side: .NET 10 and ModernUO's own `SpawnerDto` / `SpawnerJsonSerializer`
  / `DecorationList` APIs, from the bridge assembly (ADR-0012).

## ADR Dependencies

- ADR-0011: the world project and `tools\world` export gain `shard/`.
- ADR-0012: the bridge assembly is where the private shard's sync and, later,
  live apply run.
- ADR-0015: the World tab draws the objects.

## Context

### What ModernUO actually does with these files (measured in its source, 2026-09-27)

The plan (§4.7) assumed "export copies them into the running shard's `Data/`;
they take effect on the next boot". That assumption is **wrong**, and this ADR
exists largely to correct it.

- **Decoration**, `Data/Decoration/<facet>/*.cfg`, is read only by the GM
  command `[Decorate`. It needs Developer access.
  - It walks fixed folders (Britannia = Trammel + Felucca, Trammel, Felucca,
    Ilshenar, Malas, Tokuno) and creates items into the world, which the
    save then keeps.
  - It skips an entry whose item already stands at that spot
    (`DecorationList.FindItem`), so running it again adds only what is new.
  - It never removes anything.
- **Spawners**, `Data/Spawns/**/*.json` (and XML, and Nerun `.map`), are read
  only by `[ImportSpawners <glob>`.
  - Each JSON record is a `SpawnerDto` (`guid`, `name`, `location`, `map`,
    `count`, `minDelay`, `maxDelay`, `team`, `homeRange`, `walkingRange`,
    `entries[{name, maxCount, probability}]`).
  - Before placing one, it deletes existing spawners of the same type **at
    that location**. So a changed spawner is replaced, but a moved one leaves
    the old one standing, and a removed one is never removed.
- A restart re-reads **neither**: the world comes back from the save.

### What the editor needs

- **Place, move and delete**, with the world project as the source of truth
  (as ADR-0011 made it for terrain).
- **Reviewable diffs** (one object per line) and **no server format** in the
  editor (§4.8).
- **An honest path for shards that run no GUO code at all**, and a complete
  one for the private shard.

## Decision

### 1. The neutral model: `<project>/shard/objects.json`

```json
{
  "format": 1,
  "spawners": [
    {"id": "<guid>", "map": "Felucca", "x": 1165, "y": 1668, "z": 0, "count": 1,
     "min_delay": "00:05:00", "max_delay": "00:10:00", "home_range": 2, "walking_range": -1, "team": 0,
     "entries": [{"name": "Horse", "max": 1, "probability": 100}], "extra": {}}
  ],
  "items": [
    {"id": "<guid>", "map": "Felucca", "x": 1167, "y": 1666, "z": 0, "item_id": "0x0E75", "hue": "0x0000",
     "type": "Static", "props": {}, "extra": {}}
  ]
}
```

- **Map names** are the server-neutral facet names: Felucca, Trammel,
  Ilshenar, Malas, Tokuno, TerMur.
- **Every object has a GUID** made by the editor.
  - For spawners it becomes the server's spawner GUID where the server has
    one (ModernUO does).
  - For items it is GUO's own key; servers do not carry it.
- **`extra`** keeps backend-only fields through a round trip, so a read and
  write that doesn't understand a field doesn't lose it (§4.8).
- **A vendor** is a spawner with one vendor-class entry and a home range of
  0-2. The editor's vendor preset fills those in; the model has no separate
  vendor kind.
- **Written one object per line**, sorted by map, y, x, id, so an edit reviews
  as the lines it changed.

### 2. Export: native files per backend (`tools\world\backends\<name>.py`)

`tools\world export` gains `--backend` (default `UO_SHARD_BACKEND`, then
`modernuo`). For **ModernUO** it writes, under `<export>\shard\`:

| File | Contents |
|---|---|
| `Data\Spawns\guo\<project>.json` | the spawners as ModernUO `SpawnerDto` records (`$type: Spawner`), `guid` = the object's id |
| `Data\Decoration\<Map>\guo-<project>.cfg` | the items in ModernUO's decoration format (`Static 0xNNNN` or the type name, property lines, then `x y z` lines) |
| `guo_objects.json` | the manifest: every object's id, kind, map and location, and the sync step's input |
| `APPLY.txt` | the GM commands for a shard without GUO's bridge |

Each file is valid on its own for ModernUO's import commands. The manifest is
what makes deletions possible.

### 3. Sync: applying an export to a shard

**The private instance (the bridge, at boot).** `tools\editor_shard start
--objects <export>`:

- copies `shard\Data\...` into the instance's `Data\`;
- places the manifest where the bridge finds it.

After the world loads, the bridge syncs:

- **Spawners**, keyed by GUID:
  - create through `SpawnerDto.ToSpawner()`, ModernUO's own import path;
  - update in place, or move, by deleting the old one and creating the new;
  - delete GUO spawners (recorded by GUID in the instance's
    `Saves\GUO\applied.json`) that the manifest no longer has.
- **Items**, keyed by what the bridge recorded when it created them: map,
  location and item id (servers carry no GUO id). Create, delete, and move
  as delete then create.

The world save then persists the result like any GM edit. The record of what
GUO applied is kept beside the save, so a later sync knows what is its own to
delete. Nothing that GUO did not create is ever deleted.

**Any other ModernUO shard (no GUO code).** Copy `shard\Data\` into the
shard's `Distribution\Data\`, then as a GM:

- `[ImportSpawners Data/Spawns/guo/*.json`
- `[Decorate`

This adds new objects and updates spawners in place. It **cannot delete or
move**. `APPLY.txt` says so, and lists moved and deleted objects so a GM can
`[remove` them by hand.

**Live apply (after the export slice).** The bridge gains `object.put` and
`object.delete` ops (`docs/data_formats.md` §10), applying one object on the
game thread with the same code as the boot sync. §4.7's first cut, the GM
client typing `[add` / `[props` / `[remove`, is kept as the universal
fallback for shards without the bridge.

### 4. Other backends (§4.8), in this ADR's scope but not in the first slice

| Backend | Reads and writes | Sync |
|---|---|---|
| ModernUO | JSON spawners, cfg decoration | bridge (private) or GM import commands |
| ServUO | XmlSpawner `.xml` + cfg decoration | `[xmlload` / `[xmlspawner` commands via the GM client; an optional bridge script later |
| RunUO 2.x | commands only (spawners live in the binary save) | the GM client; the commands sent are logged to `shard/commands.log` as the replayable record |
| Sphere, UOX3, POL | not designed for | the adapter interface is what they would implement |

## Alternatives Considered

### Files only, and "restart to apply"

This is what the plan assumed. ModernUO does not read these files at boot, so
nothing would appear. Rejected on the evidence above.

### Server-native files as the project's format

Editing ModernUO JSON and cfg directly ties the project to one server: cfg
has no ids, so moves and deletes cannot be expressed. The neutral model keeps
ids and lets one project export to ModernUO and ServUO alike. Rejected.

### Parsing or writing the binary world save

This is the only way to see objects placed in game by hand. But it is
version-specific, and every server upgrade could corrupt a live shard.
Rejected in §4.8; confirmed here.

### Deleting by location on shards without the bridge

For example, `[remove` everything at each exported spot. That risks deleting
objects the GM placed by hand. Deletions on such shards stay a listed,
human-run step.

## Consequences

### Positive

- The project stays the source of truth, and diffs stay one object per line.
- A ModernUO shard with no GUO code can still take an export with its own
  commands (adds and updates).
- The private shard gets full create, update, move and delete, and so does
  any shard that installs the bridge. Live apply reuses the same code.

### Negative

- A third moving part (the sync) beside files and commands, and a record of
  what GUO applied (`Saves\GUO\applied.json`) that must stay with the save.
- Items are recognised by map, location and item id. A GM who moves a
  GUO-placed item by hand makes it look foreign to the next sync: the sync
  leaves it and places a new one.

## Risks

| Risk | Mitigation |
|---|---|
| The sync deletes something a GM placed | It deletes only what `applied.json` records as its own, and only on the private instance |
| ModernUO's DTO or decoration API changes | The bridge is compiled against the instance's own `Server.dll` / `UOContent.dll`; a build break is loud |
| A vendor class name does not exist on the shard | Export checks entry names against the shard's type list when it can read it (`Data/categorization.json` and the assemblies), and warns |

## Validation Criteria (the first slice, S5)

- `editor_smoke` gains a world-objects stage:
  - place one decoration item and one spawner in the World tab;
  - move one, delete one;
  - `shard/objects.json` holds exactly the result;
  - reopening the project draws them again.
- `tools\world export` writes the ModernUO files and the manifest, and
  `verify` reads them back and checks them against the model.
- On the private shard (2594): start with `--objects`, and a client logged in
  at the spot sees the decoration item and the spawner's creature. The
  evidence is the client's own world-object list (headless); a frame
  capture needs a windowed run.

## GDD Requirements Addressed

None: GUO is a port. docs/editor_plan.md §4.7, §4.8, §5 phase 6.

## Related

docs/editor_plan.md, ADR-0011, ADR-0012, ADR-0015; ModernUO
`Commands/Object Creation/Decorate.cs`,
`Engines/Spawners/Commands/ImportSpawnersCommand.cs`,
`Engines/Spawners/Json/SpawnerDto.cs`.
