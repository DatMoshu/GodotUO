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

## Screenshots

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
