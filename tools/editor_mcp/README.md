# GUO editor MCP

Controls the **editor**, independently of `tools/guo_mcp` (the running game).
The addon shares its tool host with AI chat and its discovery catalog with F3.
All Godot/data operations run on the editor thread. Mutations require approval
in GUO; native actions can show a second dialog. F3 invocation returns dispatch,
not completion of a build, save or deployment.

## Start

Editor Settings > GUO > AI > Enabled must be on. Turning that preference off
immediately closes this listener and all GUO-owned AI sessions, regardless
of MCP environment configuration. Manual authoring remains available.

Choose an unused port. In PowerShell, before opening the editor:

```powershell
$env:GUO_EDITOR_MCP_PORT = '28741'
$env:GUO_EDITOR_MCP_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
# Optional when Python is not on PATH; use your installed Python executable.
$env:GUO_EDITOR_MCP_PYTHON = (Get-Command python).Source
launchers\editor\open_project.bat
```

Keep the token in this process environment; never print or commit it. A running
editor must be restarted with these variables. A hot reload keeps the same
environment and recreates the listener. One port per editor checkout/instance.

GUO-created ACP sessions receive the bridge as a client MCP server in
`session/new` when configured. The transcript says it was offered; successful
tool use verifies actual adapter support. No paid inference is started by setup.

For an external MCP client, register a stdio server named `guo-editor` with:

- command: your Python executable
- args: the absolute path to `tools/editor_mcp/bridge.py`
- env: `GUO_EDITOR_MCP_PORT` and the same `GUO_EDITOR_MCP_TOKEN`

Use the MCP client's secret environment facility; do not put the token into a
tracked config or screenshot. MCP messages use newline-delimited JSON-RPC.

## Tools

Existing `search`, `inspect_asset`, `jump_world`; structured `editor_state`,
`editor_catalog`, `editor_search`, approved `editor_invoke`; `scene_tree`,
`scene_node`, `scene_select`, `scene_set_property`, `scene_open`; `project_files`,
`project_setting`; `multi_document`, `multi_validate`, `multi_open`,
`multi_history`, `multi_write_stage`.

`editor_catalog` pages by kind/offset/limit; `editor_search` returns stable keys.
Use a returned key for `editor_invoke`; include the original query for dynamic
ID/coordinate results. Re-resolve after menu changes. Sensitive setting labels
are filtered; no image or credential-file reader is exposed.

`multi_open` accepts an existing components JSON under this checkout's
`build/multi`, protects an unsaved active multi, and shows Multis. Use
`multi_validate`, inspect findings, then `multi_write_stage` after approval.
The last operation uses the existing writer, read-back and browser refresh.
It does not place the object on a shard. An in-game claim requires the private
shard proof flow. Stage writers can continue once started if a requester
disconnects; inspect the result before retrying.

## Verification and expansion

`python tools/editor_smoke/run.py --headless --reload` includes real loopback
transport tests against isolated tools, live editor state/F3 checks and the
existing editor regressions. `python tools/editor_mcp/test_bridge.py` checks
stdio framing and limits without a live editor.

The UI/session design and remaining domain coverage are in
[the workspace plan](../../docs/editor_ai_workspace_plan.md). This initial
endpoint provides useful operations and broad F3 discovery; it does not yet
implement every Godot or GUO edit as a typed tool.
