# Server backends for content packs

ADR-0026 section 5: packs carry data, and each server backend has one small
adapter that reads the store's neutral export, `guo/server-content@1`.
ModernUO comes first and is done end to end. The rest follow in the order
below, after ModernUO is proven.

## The contract every adapter implements

The export (`StoreServerExport`) writes one JSON file with these sections:
`items`, `tiles`, `maps`, `regions`, `decorations`, `loot`, `creatures`. It
also carries an `identity_hash` that names the deployment. An adapter must:

1. Refuse an unknown `schema` and any section it does not implement, rather
   than load part of a deployment.
2. Validate every row before applying any of them (stage, then apply).
3. Apply tiledata and map blocks before the world loads, and register items,
   loot and creatures by their `pack:component` identity.
4. Keep what it created identifiable by identity, so an update or rollback
   changes only its own objects.
5. Report the loaded `identity_hash`, so the client and the admin tools can
   check that the client's lock and the shard's deployment match.
6. Back up saves before a deployment that changes persistent objects.
   Switching assets does not undo a save migration.

## ModernUO (first, proven end to end 2026-10-02)

| | |
|---|---|
| Language | C#, .NET 10 |
| Licence | GPL-3.0 |
| Adapter | `tools/editor_shard/bridge` (`ContentPacks.cs` and friends), BSD-2-Clause, built against the dev shard's `Server.dll` |
| Done | items, tiledata, map blocks, regions, decorations, loot, creatures; `GUOPackItem` staff command; probe mode |
| Done for end to end | `tools/shard_content` deploy (export into `Data/GUO`, the descriptor), the bridge reading it there and logging `identity_hash`, the client installing and mounting the shard's lock, `prove` (ADR-0026 Validation) |
| Still to do | the dev shard (`tools/modernuo`) loading an export, not only the private copy; a deploy from the GUO editor's admin window |

## ServUO

| | |
|---|---|
| Language | C#, .NET Framework 4.x / Mono, scripts compiled by the server at start |
| Licence | GPL-2.0 |
| Adapter | a script package (`Scripts/Custom/GUO/`) ported from the ModernUO bridge |
| Differences | item serialisation uses `Serialize`/`Deserialize` overrides, not ModernUO's source generators; `TileData` and `Map` APIs differ in names; no `System.Text.Json` on .NET Framework, so use the bundled JSON reader or a small one |
| Work | port the bridge to a script package; deploy it into the private ServUO shard that `tools/servuo` already runs (pinned commit, 127.0.0.1:2596, built for the world-objects backend); a `tools/shard_content deploy --backend servuo`; the same probe and prove; a CI build against the pinned commit |

## RunUO

| | |
|---|---|
| Language | C#, .NET Framework 2.0-era scripts |
| Licence | GPL-2.0 |
| Adapter | the ServUO package, trimmed to RunUO 2.x APIs |
| Work | as ServUO; lower priority, since most RunUO shards have moved to ServUO or ModernUO |

## POL (Penultima Online)

| | |
|---|---|
| Language | C++ core; content in eScript (`.src`) and config files (`.cfg`) |
| Licence | check before shipping an adapter |
| Adapter | a POL package (`pkg/guo/`): a generator turns the export into `itemdesc.cfg` entries, `npcdesc.cfg` creatures, region config and a start script that places decorations |
| Differences | items and NPCs are config-defined, so generating config is more natural than reading JSON at runtime. Map blocks go through POL's realm tools, not at runtime. |
| Work | a generator in `tools/server_adapters/pol`; a POL dev shard tool; a probe that boots POL and checks the generated objects |

## Sphere

| | |
|---|---|
| Language | C++ core; content in `.scp` script files |
| Adapter | a generator writing `.scp` item and character definitions |
| Work | the generator plus a Sphere dev shard tool, as for POL |

## UOX3 and others

UOX3 uses JavaScript and DFN definition files. The same generator approach
applies. Further backends are added when a shard asks for one.

## Order and gates

1. ModernUO, end to end, with a playtest proof (ADR-0026 Validation).
2. ServUO: the bridge port and its dev shard tool. RunUO follows from it.
3. POL and Sphere generators.
4. UOX3 and others on request.

Each backend is done when a fresh shard of that kind loads the same starter
deployment and a GUO client sees the same items, decorations and creatures
that it sees on ModernUO.
