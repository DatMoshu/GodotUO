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

## Background interface and Admin automation

`editor_interface` reports window geometry, tabs, header layout and server/client
choices. `editor_navigate`, `editor_window_resize` and `editor_server_select` use
GUO methods without keyboard or mouse input. Resizing sets NoFocus and removes
AlwaysOnTop; a headless editor needs no desktop window.

`admin_view(section)` opens godview, settings, accounts, backups or commands.
`admin_state(section, offset, limit)` returns paged data, reply counters and
operation phases. `admin_connect` uses the selected server and workspace token;
`admin_refresh` requests fresh lists or health. Dispatch acceptance is not
completion: poll `admin_state` until the relevant counter or phase changes.

Approved mutations are `admin_save`, `admin_restart`, `admin_godview`,
`admin_account`, `admin_settings_edit`, `admin_settings_save`,
`admin_backup_create`, `admin_backup_restore` and `admin_command`. They call the
existing panels, retaining bridge authorization, account restrictions, settings
validation and managed restart/restore rules. Restore also requires `confirm`
to exactly match `name`; dangerous commands retain their confirmation word.
Passwords and secret settings require `secret_env`, naming an editor-process
variable beginning `GUO_ADMIN_AUTOMATION_`. Never supply a password in tool
arguments. Replies and approval logs redact credentials.

For a repeatable offline check, run:

```text
python tools/scenario_run/run.py editor.admin.offline --headless
```

This opens and reads every Admin section without a shard or loaded UO artwork.
Live mutations need a deliberately provisioned private workspace and bridge;
these tools do not grant an Admin role or create a server automatically.

## MCP access

Editor Settings > GUO > MCP Access has one toggle per group. These are per-user
workspace preferences, saved in `editor/mcp_access.json` under `UO_WORKSPACE_DIR`.
The editor checks the saved policy before approval and again at dispatch.

| Setting suffix (`guo/mcp_access/`) | Tools | Default |
|---|---|---|
| `read_state` | editor_state, editor_interface, admin_state | on |
| `navigate_resize` | admin_view, editor_window_resize, editor_navigate, editor_server_select; godview watch/find/select/filter/paperdoll/follow | on |
| `connect_refresh` | admin_connect, admin_refresh | on |
| `save_backup_create` | admin_save, admin_backup_create | off |
| `restart` | admin_restart | off |
| `restore_settings_save` | admin_backup_restore, admin_settings_edit, admin_settings_save | off |
| `accounts` | admin_account | off |
| `commands` | admin_command | off |
| `godview_actions` | admin_godview act_as, Go there, Bring here, Respawn, Clear | off |

Every change commits a UTC `editor_mcp_access` audit record alongside the new
permission. The Admin Audit list combines these local records with the server's
AD8 log; its filters and CSV include both. No connection is needed to record a
change. The MCP cannot edit its own permissions. Refusals name the required
setting. The AI-enabled preference remains the outer gate.

A malformed or unreadable policy denies every MCP call. Startup keeps the addon,
manual authoring and Audit UI available; the Audit panel and editor warning show
the policy problem. Settings display all groups disabled until the file is fixed.
The editor never overwrites the damaged file. Preserve a copy, restore or repair
`editor/mcp_access.json` in the per-user workspace, then reload the editor. A valid
empty policy (`{"groups":{},"audit":[]}`) restores the listed defaults, with mutation
groups off. Enable any required mutation explicitly afterward.

Changing tools also need the normal approval dialog, or the launching scenario's
explicit allowlist. `--allow-editor-tool` narrows dispatch; it cannot enable a
disabled setting. A setting changed while an approval waits is checked again.
In scripted sessions the exact-name allowlist also restricts read-only calls;
`readOnlyHint` never bypasses it. The scenario runner lists its state/discovery
reads explicitly alongside its navigation tools.
Other editor tools keep their existing per-call approval rules.

`python tools/scenario_run/run.py editor.mcp.access --headless --allow-editor-tool admin_save --no-register`
proves that even an allowlisted save is refused by default and listed navigation
runs. The editor smoke checks all defaults, persistence, explicit grants,
revocation during approval, audit records, CLI intersection and AI-off refusal.
`editor.header.review` carries the icon-toolbar tour repair from PR #37; the
Admin automation derives from PR #36 (`3d10cb1d`), selectively rebuilt on main.

Commands output needs the existing ModernUO `0005-system-message-hook.patch` in
the running server binary. `admin_state` section `commands` reports
`OutputAvailable`; false means an unpatched/stale server, even if its catalog and
help are valid. Rebuild the dependency through `launchers/shard/fetch.bat` and
`launchers/shard/build.bat` (or `.sh`), then provision the private shard from that
build and reinstall its bridge. The Admin acceptance check deliberately requires
real `[where` output and retains dangerous-command confirmation checks.
