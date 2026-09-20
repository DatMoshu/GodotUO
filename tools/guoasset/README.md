# guoasset — UO asset MCP server

Registered as the `guoasset` MCP server in `.mcp.json` at the repo root.

| | |
|---|---|
| **Upstream name** | `guoasset` (the external project project scope) |
| **Renamed here** | `guoasset`, so GUO can diverge without touching the external project |
| **Origin** | `%EXTERNAL_ROOT%\tools\mcp\guoasset-mcp` (project `ExternalAssetStudio.Mcp`) |
| **Binary** | `bin\Debug\net10.0\guoasset-mcp.exe`, built in the the external project workspace |
| **Path resolution** | `${EXTERNAL_ROOT}` from `launchers\_shared\config.bat` |
| **Licence** | the external project / ExternalAssetStudio — see that repo's `NOTICE.md` |

Nothing is vendored. GUO points at the binary the external project already builds; if the external project is
absent, the server simply fails to start and nothing else in the port breaks.

## What it is for

It renders UO art directly from the same `.mul`/`.uop` files this port reads,
which makes it the **parity oracle** for `ADR-0001`: a reference image and the
port's own output come from one source of truth.

Use: `get_image` (tile / multi composite / atlas), `export_art`,
`export_gumps`, `atlas_art`, `list_multis`, `multi_info`, `export_multi`.

## Hard rule

**Never call a write-side tool against `UO_CLIENT_DATA`.** `import_multi` and
the injection paths exist in the upstream server and are out of scope here —
GUO only ever reads the client install. See `docs\external-reference.md`.

Rendered captures derive from proprietary client data: write them to `build\`
or the scratchpad, never commit them.

## Tweaking it

Fork the C# project into this folder only if GUO needs behaviour the external project does
not. Until then, pointing at the external project's build is cheaper and stays current.
