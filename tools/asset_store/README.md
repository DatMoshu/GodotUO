# GodotUO Asset Store

Standard-library Python publisher/CDN plus a Godot Store window. The pack
contract lives in [data_formats.md](../../docs/data_formats.md#12-guo-asset-store-packs-adr-0019)
and the design in [ADR-0019](../../docs/architecture/ADR-0019-asset-store.md).

```text
python tools/asset_store/seed.py
python tools/asset_store/samples.py      (optional: sample themes, sounds, presets)
python tools/asset_store/run.py serve
python tools/asset_store/run.py publish example.zip
python tools/asset_store/run.py verify example.zip
python tools/asset_store/run.py index
```

The default catalogue is `build/store_cdn`, served on loopback port 18865.
`UO_STORE_DIR` and `UO_STORE_URL` follow the shared configuration convention.
An occupied/reserved port falls back to an ephemeral port and prints the
URL to use. Windows launchers live in `launchers/store`.

`samples.py` publishes nine sample packs (three each of theme, sound and
profile preset) so every shelf of the catalogue has something on it before
real packs exist. They are generated in code (drawn previews, synthesised
sound, CC0), carry ids starting `sample-` and the author "GodotUO sample",
and the web page marks them Sample. `seed.py` never publishes them.

`seed.py` publishes three groups:
- the ten built-in backgrounds, as `background` packs;
- the store-only screensaver, as a `screensaver` pack;
- six Store-only background loops that the client does not ship
  (`tools/bg_videos/store_loops.py`), as `background` packs.

It renders those six into `build/bg_videos/store` first if they are
missing, which takes a few minutes; `--skip-store-loops` leaves them out.
Every pack is a reproducible ZIP, so reseeding the same store is a no-op.

In GodotUO, open **Options → Video → Store**. Install a pack, reopen Options,
select its Store background and Apply. Installed packs sort first. Updates
compare numeric versions; older versions remain removable. The catalogue
and client use unfiltered previews; the client verifies preview hashes too.
Other supported pack kinds are installed as files and are not automatically
applied to game settings.

Removing the currently applied Store background resets the character's
background to built-in grey and saves the profile. Reopen Options after
removal to refresh its background choices. Other packs and built-in choices
are unaffected.

## Signed catalogues and mirrors (ADR-0026)

```text
python tools/asset_store/run.py keygen PATH                       a catalogue signing key (never commit it)
python tools/asset_store/run.py index                             signs when UO_STORE_SIGNING_KEY is set
python tools/asset_store/run.py check-index [--key ed25519:...]   verify the store folder's signed index
python tools/asset_store/run.py build-catalogue --listing packs --site site --cache C [--mirror URL] [--check]
python tools/asset_store/run.py mirror --from URL --key ed25519:... --store-dir OUT
```

- **A signed store folder.** Set `UO_STORE_SIGNING_KEY` (in
  `config.local.bat`), and `UO_STORE_CATALOGUE_ID`, `UO_STORE_CATALOGUE_TITLE`
  and `UO_STORE_BASE_URL` if the defaults do not fit. `publish` and `index`
  then write a `guo/store-index@2` index and its signature. Optional
  `listing/<id>/<version>.json` files add a provenance line and mirror URLs.
- **A catalogue repository.** `build-catalogue` reads one listing file per
  pack version, downloads and verifies each ZIP, and writes a signed index
  into `--site`. `--check` verifies only, for pull requests.
  `catalogue_repo/` is the template for the official catalogue's repository:
  its README, pull request checks, the publish workflow that signs in CI and
  deploys to GitHub Pages, the report form and the licence.
- **A mirror.** `mirror` copies a signed catalogue into a folder for any
  static web server, checking every ZIP against the signed hash. The index
  and its signature are copied byte for byte, last. Run it on a timer;
  `docs/store/hosting.md` covers serving it.

The client lists every catalogue at once, asks the player to approve a new
catalogue's key, and refuses a tampered, rolled-back, expired or re-keyed
index (`test_catalogue.py`).

## A shard's content (ADR-0026 section 4)

```text
python tools/shard_content/run.py deploy --name NAME --catalogue URL[=KEY] --pack ID --version V --bind PACK:COMPONENT=TYPE:ID ...
python tools/shard_content/run.py check [DESCRIPTOR]
python tools/shard_content/run.py serve
python tools/shard_content/run.py prove
```

`deploy` installs a pack and what it needs from the named catalogues with
the client's own installer. It writes the lock, refuses it if any client
component has no numeric slot, writes the ModernUO export into the shard's
`Data/GUO`, and writes the public descriptor `guo/shard-content@1` beside it.
Put the descriptor's address in the shard's server entry as `content`. Play
on that entry then asks, installs, and restarts GUO with the packs mounted
for that shard only. If they can't be mounted at the next start, the session
is dropped and GUO starts on the player's own files.

`prove` runs the whole path on the editor's private shard: a signed demo
catalogue, the deploy, Play through the Servers screen (the pregame probe),
and a client logged in with the packs, making the pack's item.
`test_shard_content.py` covers deploy and install without a shard.

## Verification

```text
python tools/asset_store/test_store.py
python tools/asset_store/smoke.py
dotnet build godot/GUO/GUO.csproj
launchers\dev\smoke.bat
godot-console --headless --path godot/GUO res://src/Store/StoreBackgroundProbe.tscn
```

`smoke.py` accepts `--dotnet <executable>` when the SDK is not on PATH. It
publishes a temporary fixture, starts HTTP, and runs the same C# installer
sources as the client. All temporary installs are removed.

`test_store.py` also requires Node 18+ for `web_check.mjs`; only Node built-ins
are used, with no browser, npm packages or network service. It serves four
fixture pack kinds through the stdlib HTTP server, parses and executes the
actual page script against a small DOM contract harness, and checks shelves,
search/kind filters, sample labels, details, empty/error states and literal
HTML-like metadata. HTML insertion sinks and console errors fail the test.
This is a JavaScript/DOM contract check, not CSS/layout or browser rendering
verification. Screenshots remain the visual proof.

## Client, server and combined starter packs

The [content contract](../../docs/asset_pack_ecosystem.md) describes supported
consumers, deployment locks and the remaining categories. Generate editable
original assets and a local catalogue with:

```text
python tools/asset_store/content_examples.py --out build/asset_pack_starters --store build/asset_pack_test_store --bundle build/guo-asset-pack-starters.zip
python tools/asset_store/razor_scripts.py --managed --store-dir build/asset_pack_test_store
python tools/asset_store/run.py --store-dir build/asset_pack_test_store serve
```

The content examples include client art, ASCII/Unicode bitmap fonts, authored
terrain/static blocks, server regions, decoration sets, shared collision metadata, a dependent
server item pack, and combined packs.
Published versions are immutable: regenerate into
a fresh directory when changing their contents. Installation is inactive;
numeric bindings and a verified deployment lock select runtime content.
Managed scripts use their own explicit review/approval/enable/run controls.

```text
dotnet build tools/asset_store/headless/StoreSmoke.csproj
python tools/asset_store/test_content.py
python tools/asset_store/content_smoke.py
python tools/asset_store/content_probe.py --godot <godot-console-executable> --data <your-UO-install>
```

Build the Godot C# project before the runtime probe. The probe reads your
installation, mounts temporary example packs, checks the real consumers,
and writes `build/asset_packs/runtime-pixels.png`. It removes its temporary
store afterward. `content_smoke.py` checks HTTP publication, automatic exact
dependency installation, and an idempotent second install.

For matching client/server terrain, add `--shared-world --server-export
build/server-content.json` to the runtime probe. The output must be new.
The probe reads the combined-world example on the client and exports the
same verified blocks for ModernUO. Set `UO_SERVER_CONTENT` to the absolute
export path when starting an isolated shard with the editor bridge installed;
`UO_SERVER_CONTENT_PROBE=1` checks all cells through its collision tile matrix.
This does not test player movement or synchronize client/server deployments
over the network.

Use `--collision-world` instead of `--shared-world` for the barrier example.
It also exports shared tiledata, and the isolated server probe verifies
static height and `CanFit` rejection using the installed impassable flag.

## Screenshots

`content_probe.py --deployment-ui` exercises the real ID/import/select/rollback
controls against a temporary store, including rejected consumer bindings,
unchanged live assets, startup mount and selected-dependency uninstall protection.
Add `--visual` for `build/asset_packs/deployment-ui.png`, and `--compact` for a
640x480 proof at `build/asset_packs/deployment-ui-compact.png`. Supply the same
`--godot` and `--data` arguments as the runtime probe. Original-assets recovery
is also available without the UI via `StoreSmoke deactivate-content ACTIVE`.

The opt-in `res://src/Store/StoreProof.tscn` loads the normal game scene and
constructs the real Options picker. With an installed background, it displays
that installed media outside the login gump. Set `GUO_STORE_PROOF_VIEW=store`
to show the Store window instead. Start this scene through a local engine
wrapper passed as `GODOT_CONSOLE` to `launchers/dev/screenshot.bat`, with
`--play --shot-after 300 --no-focus --cache-dir <isolated-absolute-cache>`.
Use `--screenshot-name store-client` or `store-installed-background`.
The proof defaults to 1200x800. Set `GUO_STORE_PROOF_SIZE=960x540` to
capture the compact header at a short desktop viewport. Accepted dimensions
are 640x480 through 3840x2160. This checks layout, not physical touch input
or device DPI; those still need a device pass.

After both captures exist, `python tools/asset_store/editor_proof.py` opens
the real editor without activation, displays those runtime captures on an
explicitly labelled evidence board, and captures the editor viewport. This
is a proof scene, not a Store editor dock. Existing editor sources are not
modified. Generated screenshots and logs stay under `build/screenshots`.

## The validation corpus

```
python tools\asset_store\test_corpus.py
```

`corpus/cases.json` describes 70 packs and manifests: good ones, and ones
that break a rule in `docs/data_formats.md` §12. They cover Unicode length
and case edges, hidden `.mul`/`.uop`/`.idx`/`.def` suffixes, path tricks,
the `screensaver` rules, hostile ZIP entries, and malformed JSON.

`corpus/build.py` writes them into one folder at test time (the ZIPs byte
by byte, so a case can set raw name bytes, flags, methods and modes). The
Python publisher (`pack.py`) and the C# installer (`StorePack`, through
`headless/StoreSmoke corpus`) both judge every file. Both must match
`expect`, and a C# refusal must be an exception the installer's callers
handle. CI runs it.

To add a rule, add its case here first; then make both sides pass.
