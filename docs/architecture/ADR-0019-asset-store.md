# ADR-0019: GUO Asset Store

## Status

Accepted for implementation — 2026-09-27. Local store and portable C#
installer verified; see Validation for the exact evidence.

## Decision

GUO distributes independently licensed user content as ZIP packs. The pack
and index contract is defined in [data_formats.md](../data_formats.md#12-guo-asset-store-packs-adr-0019),
written before either producer or consumer. Supported kinds are background,
theme, sound and profile-preset. Art overrides remain disabled. Installation
of theme, sound and preset packs stores their files; it does not execute
code, merge arbitrary profile JSON or replace game art.

The standard-library Python tool in `tools/asset_store` verifies, publishes,
indexes and serves immutable id/version releases from `UO_STORE_DIR`.
`index.json` and a static searchable card page are at its root. A single
HTTP byte range is supported for GET and HEAD. A seed command builds ten
reproducible CC0 packs from the shipped background loops and their original
licence notice. No UO install is consulted during publication.

`src/Store/StoreClient.cs` fetches the configured `UO_STORE_URL` index and
validates downloads into `user://store/<id>/<version>`. It has no Godot
reference: the headless smoke links these exact sources. A ZIP hash checks
the transfer; per-file hashes and matching index/manifest metadata check
its contents. Installation stages beside the destination and renames only
after every check passes. Existing versions are immutable. Removal stays
inside the selected installed version. Filesystem links, ZIP traversal,
case aliases, executable content, UO data names and oversized content are
rejected. HTTP redirects are disabled; pack URLs stay at the configured
origin and directory. HTTPS provides publisher authenticity on remote
stores; hashes alone are not signatures.

The in-client store runs on a Godot CanvasLayer and offers search, kind
filtering, install, version comparison/update and uninstall. Profile
compatibility uses `PlatformDefaults.CurrentVersion` and does not bump it.
An update adds the new version; older versions remain explicitly removable.
Delisted packs also remain removable offline.

## ADR-0016 integration

There is one marked integration block in `OptionsGump.cs`. It adds the
Store button and adapts the existing background dropdown through
`StoreOptions`, entirely implemented under `src/Store`. The existing Apply
path still writes the existing background mode/path keys. Installed choices
use the existing `image` or `video` mode and `user://store` file paths.
Low-power profiles select the pack's preview still. Reopen Options after
install/removal to refresh the choices. The renderer and its sampling state
are unchanged; no store code filters UO pixel art.

## Alternatives

- A native service or database adds deployment dependencies without helping
  this folder-based, immutable catalogue. Static files work with an ordinary
  HTTP host as well as the included local development server.
- Writing into the UO installation would mix proprietary data with user
  packs and make removal unsafe. The dedicated user store is the boundary.
- Registering packages in the renderer would violate ownership and expand
  the rendering change. The existing ADR-0016 mode/path seam is sufficient.

## Validation

- `python tools/asset_store/test_store.py`: publication immutability, invalid
  metadata, forbidden/path-traversal payloads, corruption and HTTP Range/HEAD.
- `python tools/asset_store/smoke.py`: publish and serve a fixture; use the
  actual C# client to fetch, install, hash-check, discover, compare versions,
  repeat installation, uninstall, reject corruption/incompatible profiles,
  and check staging cleanup. Exit 0/1.
- `python tools/asset_store/seed.py`: ten CC0 loops with stable ZIP timestamps.
- `dotnet build godot/GUO/GUO.csproj`: full client integration build.
- `launchers/dev/smoke.bat`: full engine, build and read-only UO data probe.
- `build/screenshots/store-page.png`: ten-card web catalogue, search and kind
  filters tested in the browser. `build/screenshots/store-installed-background.png` is captured through
  the no-focus screenshot launcher and opt-in StoreProof scene (which sizes
  the login window after startup and constructs the real Options gump).

## Operational notes

Run `python tools/asset_store/seed.py`, then `launchers/store/serve.bat` and
`launchers/store/open.bat`. Use `launchers/store/publish.bat <pack.zip>` for
another validated release. Run `python tools/asset_store/run.py index` to
rebuild the catalogue from existing packs. Set `UO_STORE_URL` for clients and
`UO_STORE_DIR` for the publisher in the shared configuration convention.
Use `serve --port <port>` if the OS reserves the default port; point clients
at that port. A worktree needs the usual shared engine/upstream paths and
an ignored classic `.sln` file for Godot's build callback.

The full dev smoke passes on the rebased main native-loader fix, including the headless editor checks. The client build has no errors and seven inherited warnings.
