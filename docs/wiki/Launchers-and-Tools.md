# Launchers and Tools

Two layers. **Launchers** are `.bat` files under `launchers\`, grouped by
job; every one of them calls `launchers\_shared\common.bat` first and then
runs an engine command or a Python tool. **Tools** are one folder per job
under `tools\`, each with a `run.py` entry point, all importing the shared
`tools\guo` package for paths and configuration. Third-party programs the
project depends on get a folder of their own with a README (`tools\godot`,
`tools\modernuo`).

On Linux, the desktop launchers (bootstrap, verify, build, smoke, play,
playtest, the editor and the dev shard) have `.sh` twins beside the `.bat`
files, sourcing `launchers/_shared/common.sh`. See [Linux](Linux.md).

## Launchers

### `_shared\`

Not launchers. `config.bat` (defaults, committed), `config.local.bat`
(yours, gitignored, from `config.local.bat.example`) and `common.bat` (the
shared logic). See [Configuration](Configuration.md).

### `game\`

| Launcher | Does |
|---|---|
| `play.bat` | **The** launcher. Runs the client against the configured shard. `--offline` loads data without a shard; `--frames N` quits after N frames; anything else goes to the client. |

### `pipeline\` (data steps, in order)

| Launcher | Does |
|---|---|
| `00_bootstrap.bat` | Fetches the pinned engine into `tools\godot` and the upstream reference into `sources\ClassicUO`. |
| `01_verify_client_data.bat` | `tools\uodata verify`: is the UO install complete, and which version is it. |
| `02_warm_cache.bat` | Optional: pre-build the decode cache. |
| `03_port_audit.bat` | `tools\port_audit`: re-measures the port and writes `docs\port_status.md`. |
| `04_world_export.bat` | `tools\world export` then `verify`: turns the editor's world project into patched map files. |

### `editor\`

| Launcher | Does |
|---|---|
| `open_project.bat` | Opens the Godot project in the editor. |
| `open_solution.bat` | Opens the generated `GUO.sln`. |
| `open_reference.bat` | Opens the upstream ClassicUO reference. |

### `shard\` (the local ModernUO server, see [Dev Shard](Dev-Shard.md))

| Launcher | Does |
|---|---|
| `fetch.bat` | Clones ModernUO at the pin and applies the patches. Once. |
| `build.bat` | Publishes it. Once, about two minutes. |
| `run.bat` | Runs it; Ctrl-C stops it. |
| `populate.bat` | Logs the owner account in and types the world generation commands. Once per new world. |

### `dev\`

| Launcher | Does |
|---|---|
| `build.bat` | `dotnet build` of the client. |
| `smoke.bat` | The five-step health check: engine, imports, C# build, client data, offline load. Run before every commit. |
| `screenshot.bat` | Boots the client with `--screenshot` into `build\screenshots`; extra flags pass through (`--play --shot-after N`). |
| `playtest.bat` | A scripted session against the shard: log in, walk, gumps, drag, speak; exit 0 only if every check passed. |
| `endurance.bat [seconds]` | The playtest, then keeps walking (five minutes by default) and compares the last stretch with the first in frame time, object count and memory. |
| `multi_client.bat` | Four scripted clients at once, tiled 2x2, one lane each: session, effects, highlight, sweep. `--only`, `--list`, `--sound`. |
| `plugin_probe.bat` | Builds a native and a managed test plugin, plays a session with both, reads their logs back. Needs MSVC. |
| `sweep.bat` | Photographs the same five places every run, for comparing renderer changes by eye. |
| `ab_compare.bat` | ClassicUO and GUO in the same places, stacked in one image. See [Parity and Drift](Parity-and-Drift.md). |
| `side_by_side.bat` | Both clients live on one monitor, in one place. |
| `render_diff.bat` | Compares a ClassicUO render dump with a GUO one. |
| `render_probe.bat`, `batcher_probe.bat`, `blend_probe.bat`, `mesh_probe.bat`, `atlas_probe.bat`, `audio_probe.bat` | The renderer and audio self-checks the ADRs cite in their Validation sections. |
| `touch_probe.bat` | Synthetic fingers through the touch layer on the desktop; 18 checks against the shard. |
| `editor_smoke.bat` | Opens the real editor headless and checks the add-on. |
| `brand_icons.bat` | Rebuilds every icon from `design\brand\guo-sigil.png`. |
| `port_errors.bat` | Clusters the C# build's errors by cause. |
| `rebuild_class_cache.bat` | Regenerates the engine's class cache when a new C# node type will not appear. |
| `clean_cache.bat` | Deletes the decode cache. |
| `sync_upstream.bat` | Reports upstream ClassicUO commits since the pin; `--pin` records the current head as reviewed. |
| `fetch_godot.bat` | Re-fetches the pinned engine on its own. |
| `build_guoasset.bat` | Builds the `guoasset` MCP server. |

### `windows\`, `android\`, `web\`

The export pipelines: `doctor.bat` (what the export needs and what this
machine has), `export.bat`, and for Android `install.bat`, `run.bat`,
`smoke.bat`, `dual_probe.bat`; for web `serve.bat` and `smoke.bat`. See
[Windows Build](Windows-Build.md), [Android Build](Android-Build.md) and, for
web, the [FAQ](FAQ.md).

## Tools

| Folder | Entry point | Does |
|---|---|---|
| `guo` | (package) | Shared configuration, paths, the map reader `uomap.py`. Every tool imports it. |
| `uodata` | `run.py verify\|list\|where` | Checks the UO install, prints the file registry and the resolved paths. |
| `port_audit` | `run.py [--out] [--json]` | Scores the port per file and tier; writes `docs\port_status.md`. CI fails if the committed report is stale. |
| `port_bulk` | `run.py --area X` | Ports a mechanical-tier area (verbatim/shim) by renamespacing. |
| `port_errors` | `run.py [--top N] [--raw]` | Clusters build errors. |
| `port_triage` | `run.py` | Sorts what remains of the port by tier and cause. |
| `sync_upstream` | `run.py [--pin] [--no-fetch] [--limit] [--at-pin]` | Upstream drift report; `--at-pin` checks the reference out at the reviewed commit. |
| `ab_compare`, `side_by_side`, `render_diff`, `render_dump` | see [Parity and Drift](Parity-and-Drift.md) | The parity tooling. |
| `multi_client` | `run.py [--only] [--list]` | The four-lane run. |
| `plugin_probe` | `run.py [--no-run]` | The plugin host check. |
| `plugin_host` | (C# project) | The bootstrap that loads managed assistants (Razor and the like) inside the client; shipped with the exported build. |
| `guoasset` | (C# MCP server) | Renders UO art from the client data as a parity reference for agents. `tools\guoasset\README.md`. |
| `brand` | `run.py [--sheet]` | Every app icon and the splash from the sigil. |
| `windows` | `run.py doctor\|preset\|export\|icon` | The Windows export and its icon check. |
| `android` | `run.py doctor\|templates\|keystore\|settings\|preset\|export\|install\|run\|logcat\|push\|smoke\|dual_probe\|displays` | The Android pipeline. |
| `web` | `run.py doctor\|preset\|export\|serve\|smoke` | The web pipeline; `doctor` and `export` print the upstream refusal. |
| `bg_videos` | `run.py [--only] [--size] [--out]` | Renders the ten built-in background loops. |
| `editor_smoke`, `editor_live`, `editor_shard`, `world`, `world_parity` | see [Editor](Editor.md) | The editor add-on's checks, the private shard instance, the world export and its parity check. |
| `modernuo` | `configure.py`, `patches\`, `config\` | The dev shard's patches and configuration templates; the checkout itself is gitignored. |
| `godot` | (README) | The pinned engine, gitignored. |

Every `run.py` answers `--help` with the same text as its README's header.
