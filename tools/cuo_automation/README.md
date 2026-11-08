# tools/cuo_automation

Test-only. An automation server compiled into our out-of-tree build of stock
ClassicUO, so GUO's test tooling can drive ClassicUO the way it drives GUO:
same wire protocol, same tool names, same environment variables. ClassicUO
ignores input posted to a background window, so this works inside the process
and needs no window focus and no desktop keyboard.

Layer: Tools. It is compiled only into ClassicUO (`tools/render_dump/inject.targets`
adds `CuoMcpHost.cs` to the `ClassicUO.Client` project), never into
`godot/GUO`, so it cannot ship in a GUO export. `sources/` is not edited. With
`GUO_MCP_PORT` or `GUO_MCP_TOKEN` unset the code does nothing: no listener, no
thread, no hook.

`CuoMcpHost.cs` is separate from `godot/GUO/src/Automation/McpHost.cs`: that
file is a Godot `Node` through and through (input events, viewport, scene tree),
so sharing it behind `#if` guards would have been most of the file. The socket
and JSON-RPC layer is the same shape; the rest is ClassicUO's.

## How it works

- Off unless both `GUO_MCP_PORT` (1024..65535) and `GUO_MCP_TOKEN` (32+
  characters) are set. IPv4 loopback only. The first line a client sends is the
  token (5 s, fixed-time compare), then newline-delimited JSON-RPC 2.0, MCP
  `2025-06-18`. `tools/scenario_run/mcp_client.py` and `tools/guo_mcp/bridge.py`
  connect unchanged.
- Socket threads never touch game state. Commands go into a queue that a pump
  drains on the game thread, once per game loop frame, through
  `GameController.EnqueueAction` (the hook `ShotDump` uses). The pump stands
  aside for a few milliseconds when another queued action is due, so it cannot
  starve ClassicUO's own.
- Input calls the handlers the SDL event filter calls (`Keyboard`,
  `UIManager.KeyboardFocusControl.InvokeKeyDown/InvokeTextInput`,
  `Scene.OnKeyDown/OnTextInput`, `Scene/UIManager.OnMouse*`) with the same
  arguments, so SDL focus plays no part. Enter, text, Enter reaches the chat
  line and the server, `[` commands included.
- The build sets `JsonSerializerIsReflectionEnabledByDefault` for ClassicUO
  only, because ClassicUO is `PublishAot` and the reply serialiser uses
  reflection.

## Tools: what matches GUO and what does not

| Tool | Against GUO |
|---|---|
| `guo_state` | Same fields: `frame`, `scene`, `width`, `height`, `player` (`map`,`x`,`y`,`z`; null until the world is entered, tested as `World.InGame`). Differences: `frame` counts game loop (`Update`) frames, not Godot process frames; `width`/`height` are the back buffer; no `audio` block (GUO-only); scene names are ClassicUO's (`LoginScene`, `GameScene`, ...). |
| `guo_wait` | Same arguments. Counts game loop frames; they slow down if the profile's "reduce FPS when inactive" is on, as the window is unfocused. |
| `guo_input` | Same arguments, tested for `key` and `text`. Key names are Godot's (`Enter`, `Escape`, `Up`, `F5`, `a`...); a bare SDL name also works. `shift`/`ctrl`/`alt` set the modifiers. `motion` and `button` work on gumps: the pointer position holds for the one call, because ClassicUO reads the real pointer back every frame, so there is no hover between calls, and a click in the world (which ClassicUO resolves from the last drawn frame) is not reliable. A gump click (motion, button down, button up on the top bar's Inventory button) was run once by hand and opened the container; drags, the wheel and world clicks were not exercised. Held keys and buttons are released when the last controller disconnects. |
| `guo_screenshot` | PNG image content of the back buffer (the whole window, gumps included), read as ClassicUO's own `TakeScreenshot` does, but into memory: no file, no journal line. Needs a rendering window; no headless mode. |
| `guo_ui` | Same envelope (`scene`, `headless`, `width`, `height`, `classicScale`, `truncated`, `controls[]`) with `system: "classic"` entries only (no Godot controls exist). Per control: `type`, `x`, `y`, `width`, `height` (back buffer pixels: logical gump coordinates times `classicScale`), `enabled`, `focused`, `text` (only `Label`s; editable fields are omitted). Controls have no `name`. |
| `guo_quit` | Same: replies, then `Game.Exit()` after two frames. ClassicUO's own exit path runs, so the profile saves. |
| `guo_overlay` | Not provided. It draws GUO's human-driver card on a Godot canvas layer. |

## Run

Build the injected ClassicUO (into `UO_BUILD_DIR`, `build/cuo/cuo.exe`), then the proof, which needs the dev
shard running and logs in as the shard owner:

```
python tools\ab_compare\run.py --build                  build ClassicUO with the injected files
python tools\cuo_automation\run.py proof --build --run-dir <dir>
launchers\dev\cuo_automation.bat proof                  the same, from the launcher
```

The proof launches the client unfocused, connects `McpClient`, waits for the
player, sends Enter / text / Enter for `[go 1602 1591` and `[globallight 0`,
takes a screenshot into `<dir>/stills/`, checks the position changed, calls
`guo_quit` and checks the process exits with code 0. `UO_SHARD_PORT` picks the
shard, `CUO_AUTOMATION_CHARACTER` the character (default: the one GUO last
played). It writes `<dir>/logs/proof.json` and `<dir>/logs/cuo.log`.

From your own code:

```python
sys.path.insert(0, "tools/cuo_automation"); import run
cuo = run.start_client(run.load_config(), log_dir)   # a connected McpClient is cuo.client
cuo.wait_player(120); cuo.chat("[go 1602 1591"); png = cuo.screenshot(); cuo.quit()
```

The MCP token is generated per run and kept in memory and the child's
environment. ClassicUO echoes its own command line, password included, into its
log; `start_client`/`Cuo.quit` scrub that line, and the proof checks the log
holds no secret. Do not copy an unscrubbed `cuo.log` anywhere.

## Tests

```
launchers\dev\cuo_automation.bat selftest      or: python -m pytest tools\cuo_automation -q
```

They need no shard and no ClassicUO build: the tool catalogue against
`McpHost.cs` (names and argument schemas identical; `guo_overlay` the only gap),
the scenario runner's tools all served, the same off-by-default and loopback
guards, the file compiled only into ClassicUO, and the log scrub. The live
proof above is the end-to-end check.
