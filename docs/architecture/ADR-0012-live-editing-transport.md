# ADR-0012: Live Edits Go Through a ModernUO Bridge Assembly, and UltimaLive to Clients

## Status

Accepted

## Date

2026-09-27

## Last Verified

2026-09-27 (UOP conversion, approved by the owner): `python
tools\editor_live\run.py --no-export` passes on the private shard with the
install alone. No export and no `files_override` are involved.
- The bridge offers all six facets. The client converts each
  `map<N>LegacyMUL.uop` to its UltimaLive copy, all six in under a second.
- Every copy equals the install block for block (map0 and map1 458,752
  blocks each, map2 57,600, map3 and map5 81,920, map4 32,761).
- The live stamp reaches the client and editor B (30 ms).
- With `--export <folder>`, the client still takes the export's `map0.mul`
  (path 1 below). A live edit on a facet other than map0 has not been run:
  the check stamps on map0 only.

2026-09-27: `python tools\editor_live\run.py` passes on the private shard.
Editor A stamps a tree through the World tab. The shard applies the block to
its own map, pushes it to 1 UltimaLive client and relays it to 1 editor. The
round trip to its acknowledgement is 51-57 ms. Editor B receives the block
35-46 ms after A sent it, and its world then holds the tree. The client logs
the UltimaLive statics write in the same second, and its frame shows the new
tree where the editor draws it. A GM command (`[where`) sent from A runs on
the shard as the client's character. `editor_smoke --headless --reload` still
passes with the UO Shard dock present.

## Decision Makers

Project owner (Moshu); GUO-Fable (plan author); GUOEditor session.

## Summary

The live tier (docs/editor_plan.md §4.5, phase 4) is three pieces:

1. **A ModernUO assembly**, `tools/editor_shard/bridge` (GUO.EditorBridge.dll),
   loaded through `Data/assemblies.json`. It speaks line-delimited JSON to
   editors (`docs/data_formats.md` §10) and UltimaLive to game clients. It
   applies blocks to the server's own `TileMatrix`, relays them between
   editors, and runs GM commands as a named online character.
2. **The editor's UO Shard dock**, sending every World-tab edit, undo and redo
   while live, and writing other editors' blocks into its world project.
3. **The ported client's UltimaLive** (`src/Game/UltimaLive.cs`), which needed
   one PORT DEVIATION to work on a UOP-only install.

The protocol is GUO's own, not CentrED+'s. It is block-based, so a CentrED+
bridge stays possible. Conflicts resolve as last write per block wins, and
every relay names its author.

## Engine Compatibility

Godot-independent on the shard side: .NET 10, compiled against the private
instance's own `Server.dll`. The editor side is plain .NET sockets on a
background thread, handed to the main thread each frame.

## ADR Dependencies

- ADR-0011 (world project: the unit sent is its block).
- ADR-0015 (the World tab; `WorldHost.ApplyOverlay` shows a received block).
- ADR-0014 (world objects) will reuse the `command` op and add object ops.

## Context

### What was measured before deciding

- **The client's UltimaLive** (a survey of `UltimaLive.cs`, then running it):
  - The server must send `0x3F/0x02` (shard name), then `0x3F/0x01` (maps).
  - The client builds its per-map CRC table only when the server sends a hash
    query (`0x3F/0xFF`). An update before any query throws (line 473). The
    bridge therefore sends one query at login.
  - The client keeps map copies in `%ProgramData%\<shard name>\`. It makes
    them from the loaded map file, and for a UOP map that path is commented
    out upstream, so on this UOP-only install it wrote **blank** maps. That was
    observed, cleaned up, and fixed by the one PORT DEVIATION below.
- **ModernUO** (a survey of its source, then running it):
  - It loads extra assemblies from `Data/assemblies.json` and calls
    `Configure`/`Initialize`.
  - `TileMatrix.SetLandBlock`/`SetStaticBlock` replace a block in memory.
  - `IncomingPackets.Register` takes a handler for `0x3F` (unregistered by
    ModernUO).
  - `Core.LoopContext.Post` marshals to the game thread.
  - `CommandSystem.Handle` runs a command as a mobile.
- **The shared dev shard** is used by other agents and a device. All of this
  was built and run against a private instance (`tools/editor_shard`,
  127.0.0.1:2594), and the bridge is only ever installed there.

## Decision

### The bridge (shard side)

- It is loaded as an assembly, not patched into ModernUO: no ModernUO source
  changes, and it is installed only into the private copy by
  `tools/editor_shard/run.py bridge`.
- Its editor listener is on 127.0.0.1 only. Reading happens on background
  threads; everything that touches the world or a `NetState` is posted to
  the game thread.
- A block updates the server's map in memory and is kept in a changed-block
  list, which a client logging in later receives after its UltimaLive setup.
  It is not written to the shard's data files: the world project is the
  source of truth, and export plus a restart (ADR-0011) make edits permanent.
- It offers every facet (`GUO_BRIDGE_MAPS`, default `0,1,2,3,4,5` as
  `tools/editor_shard` starts it). Listing a map makes the client build a copy
  for it; since the UOP conversion below, a UOP-only client builds a real copy
  of each. (Before it, only map0 was offered, because other facets would have
  been blank.)
- It passes each map's season to editors, which then draw the season the
  shard's clients see.

### The client (one PORT DEVIATION, approved by the owner 2026-09-27)

`UltimaLive.CheckForShardMapFile` must build the client's own copy of each map
the shard lists. Upstream finds nothing to copy on a UOP-only install:

- the UltimaLive loader's map readers are still empty at that point, so
  `GetMapFile` is null;
- its UOP conversion is commented out anyway.

It then writes a **blank** map. GUO's block, before upstream's logic, does
two things in order:

1. **Copy a `map<N>.mul`** that `GetUOFilePath` finds. On a MUL install this
   is the install's own map, as upstream's MUL branch copies. With
   `files_override` on a `tools/world` export, it is the exported map, edits
   included.
2. **Otherwise, convert `map<N>LegacyMUL.uop`.** Each UOP entry holds 4096
   consecutive 196-byte blocks, the MUL layout `MapLoader` itself reads.
   `ConvertUopMap` writes the entries in order to a temporary file, checks the
   size against the facet's block count, and renames it into place. A failure
   is logged and leaves no partial file, and upstream's path then runs as
   before.

Statics are MUL on UOP installs too, so upstream's own statics copy is
unchanged, as is the rest of the client's UltimaLive code. The owner chose to
keep this in GUO and not to offer it to ClassicUO.

### The editor

`ShardLink` (TCP, one JSON per line) and the UO Shard dock:
- live on/off, the bridge address (`UO_EDITOR_LIVE_HOST`/`PORT`), the editor's
  name (`UO_EDITOR_NAME`), a log of who changed what, and a GM command line;
- `WorldEditor.BlockWritten` sends each local edit, undo and redo as the whole
  block;
- `WorldEditor.ApplyRemote` writes another editor's block into the project and
  lays it over the map. It is kept out of undo and not sent on again.

## Alternatives Considered

### CentrED+'s protocol

It would work with their client and tooling. But it is a moving target, it
is a map server rather than a game shard, and it would still need UltimaLive
(or equivalent) to reach game clients. Deferred: the block-based messages
here map onto CentrED's block model if a bridge is ever wanted.

### Patching ModernUO's source

This would give direct access and no loader. It would also be a fork to
rebase on every ModernUO update, and it would touch a checkout other agents
build from. Rejected in favour of an assembly.

### A client-side overlay only (plan tier 1)

The client would read the editor's world project in-process. That is simple,
but it reaches only clients on the same machine, and the server's own map
(walkability) never changes. Kept as a possible addition; the shard tier
covers both.

## Consequences

### Positive

- An edit reaches the other editor in tens of milliseconds and the client
  in the same second, and the server's own map changes with it.
- Nothing in ModernUO's source changes; the shared shard is never involved.

### Negative

- Live edits on the shard are lost at restart unless exported (by design; the
  project holds them).
- UltimaLive copies of the whole map (90 MB for each of map0 and map1, 6-16 MB for the others) live in
  `%ProgramData%\GUO-Editor-Private\` on each client machine;
  `tools/editor_live` clears them before its check.
- A client keeps its UltimaLive copies once made. A later export (or a newer
  install) is not picked up until `%ProgramData%\<shard name>\` is cleared,
  which is upstream behaviour. `tools/editor_live` clears it before each check.
- The NPC pathing `StepCache` is not refreshed after a block changes.
  Monsters may path through a new wall until their cache chunk reloads.

## Risks

| Risk | Mitigation |
|---|---|
| Updates reach a client before its UltimaLive setup, which throws (upstream has no guard) | The bridge sends to a client only after its own 0x02/0x01/0xFF, and only on a listed map |
| A reload while live leaves the link's thread holding the old assembly | `ShardDock.Shutdown` closes the socket; that ends the thread. **Not yet tested:** a reload *while connected* |
| Many editors write one block at once | Last write wins, and the log names each writer. A lock or merge per block is future work |

## Validation Criteria

`python tools\editor_live\run.py` (see Last Verified), then
`python tools\editor_smoke\run.py --headless --reload`.

## GDD Requirements Addressed

None: GUO is a port. docs/editor_plan.md §4.5, phase 4, open questions 3
and 4 (answered: ModernUO had no sender, so the bridge is it; GUO's own
protocol for now).

## Related

`docs/data_formats.md` §10, `tools/editor_shard/README.md`,
`tools/editor_live/README.md`, ADR-0011, ADR-0015.
