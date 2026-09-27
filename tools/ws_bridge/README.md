# tools/ws_bridge -- the shard over WebSocket, for the web client

A browser cannot open a TCP socket and ModernUO speaks nothing else, so the
web client reaches the shard through this bridge:

```
browser (GUO web)  --ws://127.0.0.1:2594-->  ws_bridge  --tcp-->  shard 127.0.0.1:2593
```

It does the same job as upstream's test proxy
(`sources/ClassicUO/tools/ws/proxy.mjs`, websockify on 2594 → 2593), in
standard-library Python: no Node, no pip install. Every binary WebSocket
message carries raw UO bytes. Nothing is parsed, so the client's encryption and
the shard's compression pass through untouched.

```
launchers\web\ws_bridge.bat                 serve on UO_WS_BRIDGE_PORT -> UO_SHARD_HOST:UO_SHARD_PORT
launchers\web\ws_bridge.bat test            relay a real login to the configured shard
launchers\web\ws_bridge.bat test --fake     the same exchange against an in-process stand-in shard (CI)
python tools\ws_bridge\run.py serve --target host:port --port 2594 --allow-origin https://example.org
```

## The client side is upstream's

Nothing in GUO had to change for the protocol. Two settings in
`settings.json`:

```
"ip": "ws://127.0.0.1:2594",
"ignore_relay_ip": true
```

An `ip` that begins with `ws`/`wss` makes `NetClient` use `WebSocketWrapper`.
`ignore_relay_ip` makes the client reconnect to that same address after the
login server's relay packet, instead of the game server's raw TCP address.
That needs the login and game server to be one endpoint, which ModernUO is.

## Safety

- It listens on **loopback** unless `--listen` says otherwise, and warns
  when it does.
- Any web page can open a WebSocket to `127.0.0.1`. So a connection that
  carries an `Origin` header (a browser) is refused with `403` unless the
  origin is the local web server's (`http://127.0.0.1:UO_WEB_PORT` or
  `http://localhost:UO_WEB_PORT`) or one given with `--allow-origin`.
  Connections without an `Origin` (the desktop client, the test) are not
  browsers and are let through.
- Messages are capped at 1 MiB, the client's own receive cap.
- It is a development tool. A public `wss://` deployment needs TLS in front
  of it (a reverse proxy) and a decision about which shard it may reach;
  see `docs/web/unblock-report.md`.

## Settings

```
UO_WS_BRIDGE_PORT   default 2594    the port the bridge listens on (config.bat / guo.config)
UO_SHARD_HOST/PORT                  where it relays to (the same values the desktop client uses)
UO_WEB_PORT                         whose origin a browser page may connect from
```

## What has been run

2026-09-27, Windows 11, Python 3.12, the local ModernUO dev shard.

| Command | Result |
|---|---|
| `run.py test --fake` | PASS: a foreign Origin is refused (403); the local page's Origin is accepted; the stand-in shard's 0x82 reply arrives; a 70 KB burst arrives intact |
| `launchers\web\ws_bridge.bat test` | PASS: the shard answers a 0xEF + 0x80 login as `UO_SHARD_OWNER` with a **0xA8 server list** (46 bytes) through the bridge |

GUO itself has not connected through the bridge yet. That needs the web
build (ADR-0008) or a desktop run with the two settings above.
