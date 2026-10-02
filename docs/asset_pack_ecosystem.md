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

By default, the client reads `user://store/.active-content.json`, selected
through the Store's **Configure deployment** window. No selected file means
the original client assets. `UO_CONTENT_STORE` overrides the installed store
root and `UO_CONTENT_LOCK` overrides the lock path; environment-managed
deployments remain managed through the command-line tools.
Activation happens at archive load, before texture caches are populated;
restart to change deployments. An invalid lock or unsupported client component
refuses the deployment rather than partially activating it.

The desktop deployment window accepts explicit numeric IDs or imports an
existing lock. **Validate & select** runs every client consumer in dry-run mode
before atomically selecting the lock. A failure preserves the existing
selection and running assets. **Restore previous** revalidates before rollback;
**Use original assets** clears the selection while retaining rollback. Selected
packs and dependencies cannot be uninstalled through the store. Server export
remains a separate action and scripts retain their explicit approval lifecycle.
The selection is installation-wide, not yet negotiated per shard.

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
Music currently accepts the same PCM WAVE format as sound and loops through
the ordinary music player. Wearable JSON names `art`, `animation`, `paperdoll`
component references plus a numeric `layer`. All three references must be
declared on the component. The paperdoll binding follows the classic
50000/60000 + animation ID mapping. This sets item animation/layer/wearable
metadata; fitted body coverage still depends on the supplied frames.

Shared/server tiledata components also export to ModernUO's item metadata
table. Flags, height, name, weight, layer (server quality) and animation use
the same numeric binding and value ranges. `light` remains a client rendering
field. Unknown tile metadata fields are refused to catch authoring typos.
The collision starter combines original barrier art with shared tiledata and
a shared map block. Both sides see the same impassable flag and height.

Font JSON declares `glyphs`, each with `encoding` (`ascii` or `unicode`),
`codepoint`, and a declared PNG `image`. Unicode glyphs also accept signed
byte `offset_x`/`offset_y`. The component's numeric binding selects an existing
font slot. Glyph dimensions are 1..127 pixels. ASCII covers codepoints 32..255
and keeps classic vertical alignment; Unicode covers BMP non-surrogate
characters and uses the image's opaque pixels as a monochrome mask. Missing
glyphs retain the base font. Fonts mount before text/atlas caches are created.

Map JSON declares `blocks` with block coordinates `x`/`y`, exactly 64 row-major
`land` cells (`graphic`, `z`), and optional `statics` (`graphic`, local `x`/`y`
in 0..7, `z`, `hue`). Omitted statics preserve the base block; an empty array
clears it. The numeric binding chooses an existing facet. Overlapping authored
blocks are rejected. Generated readers are private delete-on-close temporary
files; the UO install is never modified. All consumers of the map block index
see the overlay. Subsequent shard map patches retain their existing precedence.
Client-only map components affect the client. A `shared` map component in a
combined pack is also exported for ModernUO's collision tile matrix, with the
same explicit facet binding, cells and statics. Deploy the exported server
definitions with the matching client lock; network negotiation is still pending.

Script components are left inert by the asset mount. Their separate managed
session owns review, approval, enablement, execution, revocation and logout.

## Server regions

Server-target region components declare `name`, `facet`, `priority` (0..150),
and one or more `areas` with `x`, `y`, `z`, `width`, `height`, `depth`.
Optional `music` is a declared component reference with an explicit music
binding; `enter_message` and `exit_message` are text sent to players crossing
the region. Export resolves the music ID. The adapter stages bounds and name
checks before registering ordinary ModernUO regions. Duplicate names on a
facet and out-of-facet areas are rejected. These are startup definitions;
uninstalling a ZIP does not hot-unregister a running shard's regions.
Guarded, dungeon and other specialized region policies are not yet exposed.
The private-shard probe verifies registration, name lookup and bounds. Delivery
of music and entry/exit messages to a connected player remains unverified.

## Server decoration sets

Server-target decoration entries declare a `facet` and `items`. Each placement
has a stable local `id`, an explicitly referenced static-art `graphic`, world
`x`/`y`/`z`, and `hue`. Export resolves the art binding. The adapter validates
the whole set before exposing administrator commands:

- `GUOPackDecorate pack-id:component-id` applies or updates the set.
- `GUOPackUndecorate pack-id:component-id` removes only that set's owned objects,
  including when its definition is no longer installed.

Installation and server startup do not place decorations. Stable placement IDs
preserve existing item serials on repeat application. Removed IDs are cleaned
up when the set is explicitly reapplied. Ownership uses a separate
`content_items` record inside the existing GUO world-save persistence, so the
editor's object sync does not remove pack decorations. Save the world to
persist placement/removal; switching a content lock does not undo world edits.
Current placements are static scenery, not scripted/interactable item classes.
The live probe verifies placement, ownership serialization, repeat application,
removal and preservation of an unrelated object. A separate four-process
persistence probe has verified saved serial ownership, idempotent reapplication,
saved removal, preservation of another owner's object, and saved cleanup.

To repeat that integration check, use only the disposable `tools/editor_shard`
private instance with an exported decoration sample in `UO_SERVER_CONTENT`.
Install the bridge while the instance is stopped. Set
`UO_DECORATION_PERSISTENCE_PROBE` to `seed`, `reload`, `clean`, then `verify`,
starting a fresh process for each phase. Each phase must report
`[GUO content persistence] PASS <phase>`. For the first three phases, also
wait for the subsequent `Writing world save snapshot done` log before stopping
the instance. A PASS assertion alone does not prove the asynchronous save
finished. Retain each phase's log before the next start overwrites it.
The final phase checks that no probe objects or ownership records survived
cleanup and removes its temporary serial evidence file. Clear the environment
variable afterward. These probes deliberately save the private world and use
the reserved `guo-probe` namespace; never run them on a production shard.

## Server loot tables

Server-target `loot` components contain `entries`, each with an `item`
component reference, `chance` in 0..1, and inclusive integer `min`/`max`
quantities. References must name declared server item components in the verified
dependency closure. Each entry rolls independently. Zero chance never drops;
one always drops. A table allows at most 64 entries and at most 256 items in
the sum of maximum quantities. Unknown and duplicate payload fields are refused.

The adapter stages all tables before applying the deployment. Installation and
startup never grant loot. `GUOPackLoot pack-id:component-id` generates a bag for
a game master using the same generator exposed to server gameplay callers as
`ContentPacks.GenerateLoot(identity)`. Call this on the game thread; the caller
owns the returned items and must place or delete them. Items are individual
instances because base authored item definitions do not declare stackability.
An exception during generation deletes all instances created by that roll.
Authored creatures invoke the same generator through ModernUO's death hook,
placing generated items in their normal corpse.

`sample-content-loot` depends on `sample-content-server` and provides certain,
optional and disabled drops. The portable check verifies export and rejects
invalid tables. `UO_SERVER_CONTENT_PROBE=1` verifies live generated quantities
at probability boundaries and cleanup after an injected creation failure.

## Server creatures

Server-target `creature` entries declare `name`, numeric `body`, `hue`, `sound`,
`ai` (`animal` or `melee`), `strength`, `dexterity`, `intelligence`, `hits`,
`damage_min`, `damage_max`, `armor`, `fame`, `karma`, `tactics`, `wrestling`,
and `resist`. Optional `loot` names an explicitly referenced loot component.
Stats, skills and presentation IDs are bounded; unknown/duplicate fields and
unsupported AI names are refused. Body IDs use the shard's existing numeric
namespace; custom client animations require a matching client deployment.

`GUOPackCreature pack-id:component-id` explicitly spawns a creature at the GM's
location. Installation and ordinary startup spawn nothing. The concrete
`ContentCreature` uses ModernUO's BaseCreature AI, combat, death and persistence;
its initial implementation uses aggressor targeting and 0.2/0.4 active/passive
AI timing. Spellcasting AI, taming and species-specific behaviors are not yet
part of this payload contract.

Each spawned creature saves its identity and a snapshot of its resolved loot
table and item definitions. Existing creatures retain their drops if the
deployment changes or is removed; new spawns use the currently selected pack.
The bridge assembly must remain available to load its saved creature type.
`sample-content-creature` supplies a rat using the loot starter dependency.
Live tests cover spawn fields and death/corpse drops. A three-process private
shard test also saved a living creature, reloaded it without any selected
content deployment, verified its fields and death drops, then verified saved
cleanup on another restart.

To repeat persistence testing, set `UO_CREATURE_PERSISTENCE_PROBE=seed` with
the exported sample selected on the disposable private shard. Wait for both
the phase PASS and subsequent snapshot-write completion; stop the instance.
Clear `UO_SERVER_CONTENT`, start with probe phase `reload`, and again wait for
PASS and completed snapshot writing before stopping. Start with phase `verify`
to confirm cleanup, then stop and clear the probe variable. Preserve phase
logs before the next start. Never run this saving probe on a production shard.

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
The examples cover client content, bitmap fonts, authored map blocks, a server
pack depending on the client pack, and a self-contained combined pack. They are test artwork, not finished
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
- `deactivate-content ACTIVE`: select original assets and retain the previous lock.
- `export-server STORE LOCK OUTPUT`: export supported server item, tiledata, map, region, decoration, loot and creature
  definitions with numeric IDs from the same client/server lock; output must be new.

ModernUO's private editor bridge can load that export through
`UO_SERVER_CONTENT`. GM command `GUOPackItem pack-id:component-id` creates a
declared item. `UO_SERVER_CONTENT_PROBE=1` is an opt-in isolated test that
creates then deletes each item and verifies its properties, and reads every
authored terrain/static cell from the server tile matrix. Map components are
validated and staged before any block is applied at startup. It is not a
world-save migration or an automatic install hook.

## Evidence and remaining scope

The Godot `StoreContentProbe.tscn` checks fifteen client consumers, including
five-direction animation frames through the production atlas. Original starter
pack pixels are decoded into a proof image. A private ModernUO probe verifies
the combined pack's item construction/removal. Full repository smoke passed
for the merged runtime and scripting changes. The HTTP dependency-install
smoke and ten store/web tests also pass. Art bindings must fit the original
client renderer's allocated art slots; extending that capacity remains separate work.

Font proof covers ASCII and Unicode pixels and metrics; map proof covers
terrain/static block reads, bounds rejection and delete-on-close cleanup.
A combined-world example has passed both client block reads and a live private
ModernUO collision tile-matrix probe against the same payload. Live terrain
rendering and player movement through the example still need verification.
The collision starter additionally verifies server static height and a
`CanFit` rejection against the authored impassable barrier.

Not yet implemented: other server gameplay consumers;
client region overlays; presentation activation
unification; editor authoring consumers; shard content negotiation.
Deployment controls have desktop and compact rendered proofs; physical touch
and gamepad interaction remain unverified. Animation coverage currently
proves explicit sequences/anchors, not every body/equipment mapping. A verified
envelope for one of these types does not imply executable runtime support.
