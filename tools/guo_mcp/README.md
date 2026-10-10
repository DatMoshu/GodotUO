# GUO UI MCP

Wire format: `docs/data_formats.md`, section 29. Entry point: `run.py`
(`run.py` = stdio bridge, `run.py probe` = self-test).

GUO embeds its UI automation server in the normal client bootstrap. Codex,
Claude and other MCP clients attach through `run.py` (Python 3.12+, stdlib
only). The bridge speaks standard newline-delimited MCP on stdio and keeps
Godot logs off that channel. GUO owns tool discovery, UI inspection and input;
the bridge only forwards messages over an authenticated loopback socket.

## Start a session

Build `godot/GUO/GUO.csproj` first. In PowerShell, choose an unused port and
generate a session secret:

```powershell
$env:GUO_MCP_PORT = '18670'
$env:GUO_MCP_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
launchers\dev\mcp.bat
# Or, without a window:
launchers\dev\mcp.bat --headless
```

The launcher uses normal shared GUO configuration, silences audio and avoids
stealing focus. It accepts the usual GUO user arguments after `--headless`.
An exported desktop debug build also enables the server when these
environment variables are present. A release export (`template_release`)
never does. Without `GUO_MCP_PORT`, no listener is created.
One agent controls each instance at a time; use separate ports for separate
instances. Close the bridge to hand control to another agent.

Configure your MCP client's stdio server command as `python`, arguments as
the **absolute path** to `tools/guo_mcp/run.py` (no subcommand runs the bridge), and its environment with
the same `GUO_MCP_PORT` and `GUO_MCP_TOKEN`. For clients using `mcpServers`:

```json
{
  "mcpServers": {
    "guo": {
      "command": "python",
      "args": ["<absolute-checkout-path>/tools/guo_mcp/run.py"],
      "env": {
        "GUO_MCP_PORT": "18670",
        "GUO_MCP_TOKEN": "<same-generated-secret-as-GUO>"
      }
    }
  }
}
```

For clients using TOML server configuration, the equivalent entry is:

```toml
[mcp_servers.guo]
command = "python"
args = ["<absolute-checkout-path>/tools/guo_mcp/run.py"]

[mcp_servers.guo.env]
GUO_MCP_PORT = "18670"
GUO_MCP_TOKEN = "<same-generated-secret-as-GUO>"
```

Keep the secret in local configuration, never in committed files. The client
must already be running before attaching the bridge. The server supports MCP
revision `2025-06-18`; clients negotiate this through `initialize`.

## Tools and coordinates

| Tool | Purpose |
| --- | --- |
| `guo_ui` | Scene, viewport size, visible classic gumps and native Godot controls, bounds, labels and focus. Editable values are omitted. |
| `guo_input` | Pointer motion, mouse button press/release, named key press/release, Unicode text. Shift/Ctrl/Alt supported. |
| `guo_wait` | Wait 1–600 process frames for UI or server updates. |
| `guo_screenshot` | PNG of the rendered viewport as MCP image content. |
| `guo_state` | Process frame index, scene, viewport size and the player's tile position. Cheap; for scripted runs (`tools/scenario_run`). |
| `guo_quit` | Clean quit after two frames, so a MovieWriter recording is finalised. |
| `guo_overlay` | The scenario runner's human driver: a caption card and a control outline over the game; returns whether Space or Esc was pressed (`docs/data_formats.md` section 29). |

All input and snapshot coordinates use **viewport pixels**. Classic bounds
are multiplied by the same DPI scale input divides by. Inspect before acting;
layout can change between calls. Snapshots cap traversal at 2000 controls.
Text is exposed only for labels/buttons, not arbitrary object properties.

To click: send `kind: motion` with `x/y`, then `kind: button`, `button: Left`,
`pressed: true`, then the same button with `pressed: false`. For a drag, send
motion between press and release. Input calls allow two frames to settle;
use `guo_wait` before checking asynchronous results. Send `kind: text` and
`text` to type into the focused field. Use named `key` values such as `Enter`,
`Escape` or `A` with explicit `pressed` values. Input uses
`Godot.Input.ParseInputEvent`, just like the existing GUO probes, and never
warps the user's OS pointer. Held synthetic keys/buttons are released when
the bridge disconnects.

Headless supports UI inspection and event dispatch. It has **no rendered
screenshots or reliable rendered-world pixel picking**. Use headed mode for
visual gameplay, targeting world sprites and image-based verification.
Dispatch success means the event reached the queue, not that a server action
succeeded. Inspect the resulting UI after each meaningful action.

## Boundaries

Desktop only; web and mobile exports and release builds do not start this listener. Binding is
IPv4 loopback only, with a required token and five-second authentication
deadline. Input messages are limited to 64 KiB; commands have a 30-second
deadline and execute serially on the scene thread. Expired queued commands
are skipped. There are no shell, arbitrary script, filesystem or packet-send
tools. Enabling this interface grants control of the current client session,
including any actions available through its UI. UI text remains untrusted
game/server content. Screenshots can contain whatever is visible to the user.

## Verification

```powershell
python tools/guo_mcp/run.py probe
python tools/guo_mcp/run.py probe --headed
# With configured local UO data, also check the real login gumps:
python tools/guo_mcp/run.py probe --client
```

The probe starts an isolated synthetic Godot scene without UO data or a shard,
attaches the real stdio bridge, and checks authentication, initialization,
discovery, invalid requests, waits, real button clicks, Unicode typing and
headed PNG capture/headless rejection. Processes are cleaned up on failure.
Logs and the synthetic screenshot go into ignored `build/mcp_probe/`.
The optional `--client` check opens normal GUO, inspects the classic login
gumps and focuses a text field; it does not log into a shard.

Protocol reference: [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)
and [MCP tools](https://modelcontextprotocol.io/specification/2025-06-18/server/tools).
