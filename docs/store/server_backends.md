# Server backends for content packs

ADR-0026 section 5: packs carry data, and each server backend has one small
adapter that reads the store's neutral export, `guo/server-content@1`.
ModernUO is proven end to end. The other five now have generated native adapters in
[tools/server_adapters](../../tools/server_adapters/README.md), with explicit capability gates.
ServUO/RunUO support all seven sections except unsupported tile metadata fields; POL, Sphere
and UOX3 currently support item definitions only. Native probes are not full client playtests.

## The contract every adapter implements

The export (`StoreServerExport`) writes one JSON file with these sections:
`items`, `tiles`, `maps`, `regions`, `decorations`, `loot`, `creatures`, and (ModernUO only) `spawners`. It
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
| Done | items, tiledata, map blocks, regions, decorations, loot, creatures, spawners; `GUOPackItem`, `GUOPackSpawn` staff commands; probe mode |
| Done for end to end | `tools/shard_content` deploy (export into `Data/GUO`, the descriptor), the bridge reading it there and logging `identity_hash`, the client installing and mounting the shard's lock, `prove` (ADR-0026 Validation) |
| Editor | the UO Store tab, Server content: deploy, dry run, status and rollback for a server profile (2026-10, smoke-checked against a scratch folder) |
| Still to do | the dev shard (`tools/modernuo`) loading an export, not only the private copy |

## ServUO

| | |
|---|---|
| Language | C#, .NET Framework 4.x / Mono, scripts compiled by the server at start |
| Licence | GPL-2.0 |
| Adapter | a generated C# package (`Scripts/Custom/GUO/`) using the same neutral export |
| Differences | item serialisation uses `Serialize`/`Deserialize` overrides, not ModernUO's source generators; `TileData` and `Map` APIs differ in names; no runtime JSON dependency: validated data becomes generated C# initializers |
| Implemented | generated C# package, `deploy --backend servuo`, all-section compile/runtime and persistence probes; CI compile against the pinned commit |
| Remaining | live GUO client playtest against a deployed shard |

## RunUO

| | |
|---|---|
| Language | C#, .NET Framework 4.5.2 at the validated pin |
| Licence | GPL-2.0 |
| Adapter | the shared generated package, compiled and probed against the pinned RunUO APIs |
| Implemented | generated C# package, `deploy --backend runuo`, full upstream compile and real API/persistence probes |
| Remaining | live GUO client playtest |

## POL (Penultima Online)

| | |
|---|---|
| Language | C++ core; content in eScript (`.src`) and config files (`.cfg`) |
| Licence | check before shipping an adapter |
| Adapter | a POL package (`pkg/guo_content/`): generated `itemdesc.cfg` entries with explicit object types; other sections are refused |
| Differences | items and NPCs are config-defined, so generating config is more natural than reading JSON at runtime. Map blocks go through POL's realm tools, not at runtime. |
| Implemented | native itemdesc/package generator, explicit object-type allocation, POL config parser proof |
| Remaining | creatures, loot, regions, decorations, realm/map and tile integration; live client proof |

## Sphere

| | |
|---|---|
| Language | C++ core; content in `.scp` script files |
| Adapter | a generator writing `.scp` item and graphic-base definitions; character definitions remain pending |
| Implemented | native item generator with new graphic base definitions and collision checks; native item creation proof |
| Remaining | other server sections and full live client proof |

## UOX3 and others

UOX3 uses JavaScript and DFN definition files. The same generator approach
is implemented for item definitions, with native UOX3 item creation tested. Other server
sections and a full live client proof remain. Further backends are added when a shard asks for one.

## Order and gates

1. ModernUO, end to end, with a playtest proof (ADR-0026 Validation).
2. ServUO: the bridge port and its dev shard tool. RunUO follows from it.
3. POL and Sphere generators.
4. UOX3 and others on request.

Each backend is done when a fresh shard of that kind loads the same starter
deployment and a GUO client sees the same items, decorations and creatures
that it sees on ModernUO.

## Deploying from the editor

The GUO editor's UO Store tab, *Server content*, runs `tools/shard_content` for a server profile of the run bar's
list. The profile's backend picks the adapter (ModernUO, ServUO, RunUO, POL, Sphere, UOX3); a `custom` profile has
none. It shows the log, what each profile has deployed (`status`), and rolls back ModernUO from `Data/GUO/previous`
(native adapters keep every replaced file in `Data/GUO/revisions`; deploy the older pack version again).
