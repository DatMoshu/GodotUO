# tools/web -- GUO as a web page

Exports the client for the web, serves it (with the player's own UO install)
from this PC, and smoke-tests it in headless Chrome and Firefox. The decision
record is [ADR-0008](../../docs/architecture/ADR-0008-web-target.md) and its
amendment 1; the research behind it is
[docs/web/unblock-report.md](../../docs/web/unblock-report.md).

The pinned Godot 4.7.2 mono **refuses** a C# web export. This tool exports
with a community 4.7.2 build that can do it (godotengine/godot#106125, set
up in [tools/godot_web](../godot_web/README.md)), kept apart from the pinned
engine with its own .NET SDK and NuGet cache.

```
launchers\web\doctor.bat      what a web export needs, and what is here
launchers\web\export.bat      export build\web\GUO.html with the fork (~10 min)
launchers\web\serve.bat       serve build\web + the install at /uo/, COOP/COEP, 127.0.0.1 only
launchers\web\ws_bridge.bat   the shard over WebSocket (tools\ws_bridge), for logging in
launchers\web\smoke.bat       export + serve + Chrome and Firefox + wait for the login gump
```

Everything is `python tools\web\run.py <command>` underneath; `preset` only
renders the export preset.

## Playing it locally

```
launchers\shard\run.bat           the dev shard (or any shard: UO_SHARD_HOST/PORT)
launchers\web\ws_bridge.bat       ws://127.0.0.1:2594 -> the shard
launchers\web\serve.bat           http://127.0.0.1:8060/GUO.html
```

Open `http://127.0.0.1:8060/GUO.html`. Page parameters, all optional:
`?data=uo/` (where the install is served; `none` mounts nothing),
`&host=ws://h&port=2594` (the bridge; defaults come from `serve`),
`&arg=--foo` (any client argument, repeatable).

## Play on a phone

LAN mode lets a phone or tablet on the same network play from this PC. It is
**off by default** and stays off until you turn it on.

```
set UO_WEB_LAN=1                  in launchers\_shared\config.local.bat (or pass --lan)
launchers\shard\run.bat           the shard, as before
launchers\web\ws_bridge.bat       now wss://<this PC>:2594
launchers\web\serve.bat           now https://<this PC>:8060/GUO.html
```

In LAN mode both servers listen on this PC's private network address only,
never on every interface, and turn away any connection that does not come from
a private address: they cannot be reached from the internet, even through a
router port-forward. Other devices on your network can reach them, and `serve`
hands them your UO install, so use it on a network you trust. `serve` prints
the addresses to open; if it picks the wrong network card, set
`UO_WEB_LAN_HOST` in `config.local.bat` to this PC's address on the right one.

A phone's browser runs the game only from a secure (https) page, so the first
`serve --lan` makes a small certificate authority for this PC, kept in
`build\web\lan_certs` (never committed). The phone has to trust it, once:

1. Start the bridge and `serve` as above. When Windows asks, allow Python on
   **private** networks only.
2. On the phone, open `https://<this PC>:8060/guo-ca.crt` (use one of the
   addresses `serve` prints). The browser warns that the page is not secure;
   continue, and the file downloads.
3. Install it as a CA certificate. On Android: Settings, Security and
   privacy, More security settings, Encryption and credentials, Install a
   certificate, **CA certificate**, then pick `guo-ca.crt`. (On an iPhone:
   open the downloaded profile in Settings, install it, then turn it on under
   General, About, Certificate Trust Settings.)
4. Close the browser tab and open `https://<this PC>:8060/GUO.html`. It loads
   with no warning, and the page talks to the bridge over `wss` on the same
   host by itself.

The certificate names this PC's address, its name and `<name>.local`, and is
renewed on its own when the address changes or it nears expiry; the CA stays
the same, so the phone installs it only once. Delete `build\web\lan_certs`
to start over (the phone must then install the new CA, and should remove the
old one: Encryption and credentials, User credentials).

## Settings

```
UO_WEB_PORT          default 8060      the page server's port
UO_WS_BRIDGE_PORT    default 2594      the WebSocket bridge's port
UO_WEB_GODOT         tools\godot_web\...\*_console.exe   the fork that can export C# to the web
UO_CLIENT_DATA                         the install serve hands to the page (this PC only)
UO_CLIENT_VERSION                      told to the page through /uo/_index.json
UO_WEB_LAN           default 0         1: serve and the bridge in LAN mode (https, wss), for a phone
UO_WEB_LAN_HOST      default empty     this PC's LAN address, when LAN mode picks the wrong one
```

## How the pieces fit

- **Export** runs the fork's console with its private .NET SDK and NuGet
  cache (`Paths.web_env`), fails on any `ERROR:` line (a failed C# build
  still writes a page), then makes two edits to the engine's JS, each anchored
  on text that must occur exactly once (`PAGE_PATCHES`): it exposes
  Emscripten's `FS` as `Module.guoFS`, and calls
  `window.guoBeforeMain(Module, args)` right before `main()`. It copies
  `guo_data.js` next to the page; the preset loads it from `<head>`.
- **guo_data.js** mounts the install at `/uo` as lazy read-only files: sizes
  from `/uo/_index.json`, bytes fetched on first read in 1 MiB chunks through
  a *byte source*, with a 384 MiB LRU. Today's source is HTTP Range from
  `serve`. A folder the player picks, or a shard that hosts its own client
  files, plug in behind the same `read()`. It also builds the client's
  arguments: user args after `--`, the main pack by absolute path (the client
  changes directory at startup), `--client-data /uo`, the bridge host and port.
- **The client** is unchanged above its readers. In a browser: `MMFileReader`
  reads through the stream, not a mapping; zlib is `ManagedInflate` (the
  template links no `System.IO.Compression.Native`); the socket is
  `GodotWebSocketWrapper` over Godot's `WebSocketPeer`; `ignore_relay_ip` is
  on. Each is a marked `PORT DEVIATION (GUO)`, off on the desktop.
- **serve** answers `/uo/_index.json` and ranged `GET /uo/<file>`, from
  `UO_CLIENT_DATA` by default, on 127.0.0.1 only (in LAN mode: https on the
  LAN address, private peers only, plus `/guo-ca.crt`; `tools/guo/lan.py`);
  the page's own files get COOP/COEP (the threaded template needs
  `SharedArrayBuffer`). The index gives the bridge's port but no host: the
  page uses the host it was served from, with `wss` when it is on https.
- **smoke** uses Python Playwright (system Chrome, and Firefox from
  `tools\godot_web\browsers`), passes `?arg=--login-probe-stay`, and waits
  for `[GUO] login probe: ok`; FATAL lines and C# exceptions fail it fast.
  `--query` and `--wait-for` drive other probes (the shard login below).
  Output: `build\web\smoke_<browser>.png` and `.txt`.

## What has actually been run

2026-09-27, Windows 11, the fork in tools/godot_web (Godot 4.7.2 mono +
#106125, Emscripten 6.0.5, threaded), private .NET SDK 10.0.301 with
wasm-tools-net9 (runtime 9.0.19).

| What | Result |
|---|---|
| `run.py export` | exit 0 in about 10 min, most of it the SDK's `emcc` relink, which cannot be turned off while globalization is invariant; 67 MB wasm, 68 MB pck |
| `run.py smoke` | **login gump rendered in Chrome (57 s) and Firefox (235 s)**, pixel-identical; 250 range requests, 227 MiB of the 2.6 GB install read |
| `serve` + `curl` | `/uo/_index.json` lists 520 files (2,588 MiB); `Range: bytes=10-19` gives `206` with `Content-Range`; `..%2F..` gives `404` |
| `dotnet build` / `launchers\dev\smoke.bat` | 0 errors / OK: nothing changes on the desktop |
| `--offline` with `GUO_NO_MMAP=1 GUO_ZLIB=managed` | loads every archive, same log as without |
