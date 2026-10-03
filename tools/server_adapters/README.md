# Native server content adapters

`guo/server-content@1` is the shared, numeric-bound server export. These adapters turn it into native server definitions. Pack data never supplies executable server code: C# and script text come from these trusted generators, with strict field validation and escaped literals.

| Backend | Supported server sections | Evidence |
|---|---|---|
| ServUO | items, loot, creatures, decorations, regions, maps, tiledata | Full upstream build and runtime API/persistence probe |
| RunUO | items, loot, creatures, decorations, regions, maps, tiledata | Full upstream build and runtime API/persistence probe |
| POL | items | POL 100.2.0 ecompile/runecl parsed the generated configuration |
| Sphere X | items | Native nightly runtime created the authored item with matching graphic, name, weight and mobility |
| UOX3 | items | Native 0.99.7a runtime created the authored item with matching graphic, name, weight and mobility |

The C# tile adapters reject `animation` and `light` fields: those fields are absent from these servers' ItemData APIs. POL, Sphere and UOX3 reject other nonempty sections. Nothing is silently omitted. All backends still need a live GUO client login/world/save/restart proof; these checks are not that proof. ModernUO retains its existing bridge and end-to-end validation.

## Deploy a pack

Stop the selected server and back up its saves. From the repository, use the existing signed-catalogue deployment path:

```powershell
python tools/shard_content/run.py deploy --backend servuo --name "My test shard" --catalogue http://127.0.0.1:18865/ --pack sample-content-combined --version 1.0.0 --bind sample-content-combined:stone=static:6001 --shard-dir <server-folder>
```

Use `runuo`, `pol`, `sphere`, or `uox3` for the other backends. Non-ModernUO deployments require an explicit server folder. This installs native definitions, the neutral export, and the matching public descriptor. Configure the server entry's content URL as before. POL also requires `--adapter-slots slots.json`, with reserved, unique object types, for example:

```json
{ "sample-content-combined:stone-item": 327680 }
```

The graphic slot and POL object type are different namespaces. Reserve the object type against your shard's existing packages; the tool does not allocate or reuse types by list position.

Native paths and activation:

- ServUO/RunUO: `Scripts/Custom/GUO/GUOContent.cs`. Rebuild scripts and restart. ServUO's dynamic compiler can build on startup; shards with it disabled must build explicitly. Commands: `GUOPackItem`, `GUOPackLoot`, `GUOPackCreature`, `GUOPackDecorate`, `GUOPackUndecorate`, each followed by `pack:component`. Decoration commands require Administrator; creation requires GameMaster.
- POL: `pkg/guo_content/pkg.cfg` and `pkg/guo_content/config/itemdesc.cfg`. Restart with packages enabled. The item can be created by its reserved object type using your distribution's staff tools.
- Sphere: `scripts/guo_content.scp`. Add this file to `[RESOURCES]` in your existing `spheretables.scp`, then restart. The generator supplies numeric bases for new graphics. If the shard already defines a graphic, pass `--sphere-existing-graphic 6001` to reuse it; collision/missing-base checks run before installation. Existing script includes and type definitions remain owned by the shard operator.
- UOX3: `dfndata/items/guo_content.dfn`, relative to the distribution root where `uox.ini` lives. The standard `DEFSDIRECTORY=./dfndata/` loads it on restart. For a custom definitions directory, generate a bundle and integrate it into that directory explicitly.

`Data/GUO/adapter-install.json` records the backend, deployment identity, native identity mapping and checksums. Stable native names derive from pack/component identity, not item order or the current art slot. POL stores an identity CProp; Sphere sets an identity tag; UOX3 preserves its DFN section ID. C# entities serialize their identity; creatures serialize their loot/item snapshot, so already-saved creatures do not depend on a removed pack's current definitions. Keep the generated C# persistence classes installed while saves contain GUO entities, even if the next deployment has empty definition lists.

## Generate first for review

```powershell
python tools/server_adapters/run.py capabilities
python tools/server_adapters/run.py generate --backend sphere --export <server-content.json> --out build/sphere-review
python tools/server_adapters/run.py install --bundle build/sphere-review --shard-dir <server-folder>
```

Output must be a fresh directory. Standalone installation publishes only native files. Once a full server/client deployment exists, use `shard_content deploy` for updates or rollback to an older pack version so server files and the client descriptor stay together.

All rows are validated before output. Unsupported sections, unknown fields, duplicate identities, bad references, invalid ranges, unsafe native names and unrepresentable weights fail the operation. Native names currently accept plain ASCII letters, digits and basic punctuation. Sphere weights must be exact tenths of a stone; UOX3 uses hundredths; POL receives a rational weight. C# preserves the supplied double.

Installation takes an exclusive lock, verifies existing owned checksums, refuses unowned/edited files, retains revision backups under `Data/GUO/revisions`, and rolls back replaced files if publication fails. It refuses linked paths and the configured client installation. It does not modify world saves. A power loss is not a transaction across the filesystem: inspect the retained revision and lock before recovery. An adapter installation lock left after a crash is intentionally not removed automatically.

C# decorations are placed only by staff command, never on startup. Reapplying reconciles the adapter's own saved `PackDecoration` objects; removal cannot delete ordinary shard items. Map and tile overrides are startup-only; changing or removing them requires restart. Creature instances and existing player items retain their saved values until explicitly migrated.

## Verification

```powershell
python tools/server_adapters/tests/test_adapters.py
python tools/shard_content/test_shard_content.py
python tools/server_adapters/prove_csharp.py --backend servuo --out build/servuo-proof
python tools/server_adapters/prove_csharp.py --backend runuo --out build/runuo-proof
```

The last two fetch the exact public revisions in `upstreams.json` into the fresh output directory, compile the complete upstream core/scripts, and execute the real-API probe. They require Windows, .NET SDK and Framework compiler, and configured `UO_CLIENT_DATA`. `--compile-only` needs no game data and runs in CI. Reports distinguish compilation from runtime execution. Runtime checks cover all seven sections, loot boundaries, item/creature/decoration serialization, surviving definition removal, idempotent decoration placement and unrelated-object preservation.

Native format references: [POL configuration](https://docs.polserver.com/pol100/configfiles.php), [Sphere item definitions](https://wiki.spherecommunity.net/index.php/ITEMDEF), [UOX3 item parser](https://github.com/UOX3DevTeam/UOX3/blob/master/source/items.cpp). Native runtime distributions used locally remain ignored build artifacts, not vendored dependencies. Adapter source is BSD-2-Clause; follow each server's licence when distributing a combined build.
