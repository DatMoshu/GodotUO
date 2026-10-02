# GUO content packs

Implementation in progress. This contract extends ADR-0019; v1 presentation
packs remain supported. Acceptance of a package is not proof that every
component has a runtime consumer. Unsupported consumers must refuse activation.

## Version 2 envelope

`guo/store-pack@2` uses the existing identity, version, author, licence,
preview, files and minimum profile fields, with `kind: content` and:

- `target`: `client`, `server`, or `combined`.
- `dependencies`: object mapping pack IDs to exact three-part versions.
- `components`: nonempty array of typed content records.
- Each component has `id` (local pack-ID syntax), `type`, `target`
  (`client`, `server`, `shared`), and `entry` (declared payload path).
- Optional `references` lists `pack-id:component-id` identities. External
  references require a declared dependency. Local references must resolve.
- Script components additionally declare `language`, `runtime`,
  `runtime_version`, and `capabilities`; installation never executes them.

Full component identity is `pack-id:component-id`. Targets describe deployment,
not permission to execute. Combined releases must contain both client and
server components, or a shared component. Client/server-only releases may
contain their own target and shared components only.

Content types: static, land, texmap, tiledata, gump, animation, wearable,
hue, sound, music, effect, light, translation, font, map, region, multi,
decoration, item, crafting, loot, vendor, creature, spawner, encounter,
quest, dialogue, presentation, authoring, script.

All existing archive traversal, link, size, hash, licence, and proprietary
data exclusions remain. V2 carries authored source assets, never merged UO
archives. Typed JSON entry files describe complex content and reference
payloads. Existing UO data is read locally when producing runtime exports.

## Activation foundations

Resolve exact dependencies before activation; reject cycles, missing references,
duplicate IDs and unsupported consumer versions. Installation is inert.
Shard locks pin pack versions and hashes, component identities, and explicit
numeric IDs within each UO namespace. Never infer free IDs from a globally
assumed range or renumber saved content on update. Overrides must be explicit.
Stage a complete deployment before switching its active pointer; preserve the
previous deployment for rollback. Server save migrations need separate backups
and cannot be reversed merely by switching assets.

## Required evidence

Publisher and C# installer must agree on valid and invalid v2 envelopes.
Examples must use original generated content and publish through the real
store. Each asset consumer needs a decode/application test, with a rendered
or live-server proof where behavior depends on those systems. Combined packs
need matching client/server IDs. Unsupported types stay visibly inactive.
