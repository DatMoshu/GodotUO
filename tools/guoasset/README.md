# guoasset — the parity reference renderer

A small MCP server that renders UO art straight from your client install:
land and static tiles, gumps, multis, and atlases of many ids. It is the
ground truth `/parity-check` compares GUO's frames against.

| | |
|---|---|
| **Decodes with** | Upstream ClassicUO's `ClassicUO.Assets`, compiled from the pinned checkout in `sources/` |
| **Runtime** | .NET 10 (upstream's target; the client itself stays on .NET 8) |
| **Output** | `build\guoasset\out\` unless a folder is given |
| **Licence** | BSD 2-Clause, like the rest of this repository |
| **Registered by** | `.mcp.json` at the repo root, as `guoasset` |

## Why upstream's decoder and not GUO's

A reference decoded by the code under test cannot catch that code's bugs.
GUO's `src/Assets` is a port of the same loaders; if a port slip corrupted a
colour or an offset, a reference built on it would be corrupted the same way
and the check would pass. Compiling upstream's own loaders from `sources/`
keeps the reference independent while still reading the same bytes.

## Build

```bat
launchers\dev\sync_upstream.bat     REM once: the upstream checkout
launchers\dev\build_guoasset.bat    REM -> build\guoasset\bin\GUO.AssetMcp\release\guoasset.dll
```

The build uses an artifacts path under `build\guoasset`, so the upstream
projects it compiles leave nothing inside `sources\`. On Windows it copies
upstream's `zlib.dll` next to the server; nothing else native is needed.

Claude Code starts it from `.mcp.json` (`dotnet build/.../guoasset.dll`,
relative to the repo root). Until it is built, the server fails to start and
nothing else is affected.

## Configuration

None of its own. It reads `UO_CLIENT_DATA` and `UO_CLIENT_VERSION` the way
every GUO tool does: the environment, then `launchers\_shared\config.local.bat`,
then `launchers\_shared\config.bat`. Without `UO_CLIENT_VERSION` it reads the
version stamped in `client.exe`, as upstream does. Every tool also takes an
explicit `clientPath`.

## Tools

All read-only.

| Tool | What it does |
|---|---|
| `locate` | Which install and version it reads, and where renders go. A connectivity check. |
| `get_image` | One PNG: `tile` + `kind` (land, static, gump), `multi` (isometric composite), or `ids` + `kind` (atlas). |
| `atlas_art` | A range or list of ids packed into one sheet, with a JSON manifest of frames. |
| `export_art` | One PNG per non-empty id in a range: `Land_0xNNNN.png`, `Static_0xNNNN.png`, `Gump_0xNNNN.png`. |
| `list_multis` | Every populated multi with its part count and bounds. Slow: reads them all. |
| `multi_info` | One multi's parts, in file order: item id, offset, visibility. |
| `search_tiles` | Tiles whose tiledata name contains a keyword. |

Pixels are copied out exactly and written as PNG without any resampling.

The multi composite stacks parts in the order the multi lists them. It shows
a multi's shape; it is **not** a draw-order reference, because the client
sorts by depth and this does not. Compare draw order against ClassicUO with
`launchers\dev\ab_compare.bat` instead.

## Rules

- **It never writes to the client install**, and must not grow a tool that
  does. GUO only ever reads `UO_CLIENT_DATA`.
- **Renders are artefacts, never commits.** They derive from proprietary
  client data. Keep them in `build\` or a scratchpad.
