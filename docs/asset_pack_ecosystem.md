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

## Client activation (initial consumers)

Set `UO_CONTENT_STORE` to an installed store root and `UO_CONTENT_LOCK` to an
explicit content-lock JSON. Activation happens at archive load, before texture
caches are populated; restart to change deployments. Missing settings leave the
original client path unchanged. An invalid lock or unsupported client component
refuses the deployment rather than partially activating it.

The lock binds namespaced identities to numeric IDs per asset type. Original
PNG entries support static, land, texmap and gump. Land is 44x44, texmaps are
64x64 or 128x128, static images at most 1024x1024, gumps at most 2048x2048.
Images are quantized to classic 15-bit colors; land pixels outside the diamond
are transparent. Hue JSON carries 32 integer `colors` in 0..32767. Translation
JSON carries `locale` and a `strings` object of numeric cliloc IDs to text.
Matching locale entries override clilocs; other locales remain inactive.
Authored translation files must not use the forbidden proprietary cliloc
basename. These loose source formats are not new proprietary archive formats.

Sound entries are PCM RIFF WAVE at 22050 Hz, mono, 16 bit. Multi JSON has
`items` rows with `graphic`, `x`, `y`, `z`, `visible`. Tiledata JSON patches
explicit static fields (`name`, `flags`, `height`, `weight`, `layer`,
`animation`, `light`) while preserving unspecified base fields. Light PNGs
follow the same verified image path. Animation JSON declares `group_type`
(the existing animation group enum) and `sequences` of `action`, `direction`
(0..4, the engine supplies mirrored facings), and `frames` of `image`,
`center_x`, `center_y`. Frame images must be declared files in the pack.
Playback retains the existing action timing and atlas/picking pipeline.

## Required evidence

Publisher and C# installer must agree on valid and invalid v2 envelopes.
Examples must use original generated content and publish through the real
store. Each asset consumer needs a decode/application test, with a rendered
or live-server proof where behavior depends on those systems. Combined packs
need matching client/server IDs. Unsupported types stay visibly inactive.

## Starter packs and tools

`python tools/asset_store/content_examples.py --out build/content_examples`
creates editable source directories plus reproducible original CC0 ZIPs.
Add `--store build/content_store` to publish through the real validator/index.
The three examples cover client content, a server pack depending on the client
pack, and a self-contained combined pack. They are test artwork, not finished
production art. When changing a published example, increment its version or
publish into a fresh test store; releases remain immutable.

The portable tool is built with
`dotnet build tools/asset_store/headless/StoreSmoke.csproj`. Invoke its DLL at
`tools/asset_store/headless/bin/Debug/net8.0/StoreSmoke.dll` with:

- `extract-content ZIP STORE`: verify and install a local authoring ZIP.
- `content-lock STORE PACK VERSION OUTPUT`: pin the verified dependency closure.
  Add explicit `bindings` of `pack:component` to `{type, id}`.
- `activate-content STORE CANDIDATE ACTIVE`: atomically select a verified lock,
  keeping `ACTIVE.previous`. This selects a deployment; consumers load it on
  restart and may refuse unsupported payloads. It never executes scripts.
- `rollback-content STORE ACTIVE`: reverify and select the previous lock.
- `export-server STORE LOCK OUTPUT`: export supported server item definitions
  with graphic IDs from the same client/server lock; output must be new.

ModernUO's private editor bridge can load that export through
`UO_SERVER_CONTENT`. GM command `GUOPackItem pack-id:component-id` creates a
declared item. `UO_SERVER_CONTENT_PROBE=1` is an opt-in isolated test that
creates then deletes each item and verifies its properties. It is not a
world-save migration or an automatic install hook.

## Evidence and remaining scope

The Godot `StoreContentProbe.tscn` checks eleven client consumers, including
five-direction animation frames through the production atlas. Original starter
pack pixels are decoded into a proof image. A private ModernUO probe verifies
the combined pack's item construction/removal. Full repository smoke passed
for the envelope foundation. Final runtime changes need a fresh full smoke.

Not yet implemented: other server gameplay consumers; wearable bundles;
fonts; map/region overlays as store components; music; presentation activation
unification; editor authoring consumers; shard content negotiation; automatic
dependency download; full store activation UI. Animation coverage currently
proves explicit sequences/anchors, not every body/equipment mapping. A verified
envelope for one of these types does not imply executable runtime support.
