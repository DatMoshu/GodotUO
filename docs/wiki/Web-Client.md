# Web Client

GUO runs in a web browser. The same C# client as on the desktop logs in to a
shard, enters the world and plays. On 2026-09-27, in headless Chrome, it
created a character on the local dev shard, walked in Valoria with footstep
sounds, opened the backpack and moved an item. Firefox is verified as far as
the login screen. This page is the short version; the decision record is
ADR-0008 (Accepted, Amendment 1), and the how-to is `tools/web/README.md`.

Two things make it different from the other builds:

- **The engine is a community build.** Godot 4.7 cannot export C# to the web
  (godotengine/godot#70796). GUO exports with a community Windows build of
  Godot **4.7.2** that includes the open pull request #106125. It is unsigned
  and not from the Godot project. It lives, gitignored, in `tools/godot_web`,
  kept apart from the pinned engine; its README gives the origin and the
  SHA-256.
- **Your UO install stays on your PC.** GUO can never ship Ultima Online's
  files. The page reads your own install from a small server on your own
  PC, over HTTP, a piece at a time as the client needs it. Nothing is
  uploaded anywhere.

```bat
launchers\web\doctor.bat      REM what the web export needs, and what this PC has
launchers\web\export.bat      REM export build\web\GUO.html with the community build (~10 min)
launchers\web\serve.bat       REM the page + your install at /uo/, on 127.0.0.1 only
launchers\web\ws_bridge.bat   REM the shard over WebSocket (a browser cannot open TCP)
launchers\web\smoke.bat       REM export + serve + Chrome and Firefox + wait for the login screen
```

## Setup (once)

1. **The community engine.** Follow `tools/godot_web/README.md`: download
   the 4.7.2 release zip, check its SHA-256, unzip it self-contained, put its
   web templates in its own `editor_data`, and add a private .NET SDK with the
   wasm workload. **Do not run its `install.bat`**: it writes into the same
   template folder and NuGet feed as the pinned engine.
2. **Your install and shard**, as for the desktop: `UO_CLIENT_DATA`,
   `UO_CLIENT_VERSION`, `UO_SHARD_HOST` / `UO_SHARD_PORT` in
   `launchers\_shared\config.local.bat`.
3. `launchers\web\doctor.bat` should report no `MISS`.

## Play

```bat
launchers\shard\run.bat           REM the dev shard (or point UO_SHARD_HOST at another)
launchers\web\export.bat          REM once per client change
launchers\web\ws_bridge.bat       REM leave it running: ws://127.0.0.1:2594 -> the shard
launchers\web\serve.bat           REM leave it running
```

Then open `http://127.0.0.1:8060/GUO.html` in Chrome or Firefox. Optional
page parameters: `?host=ws://...&port=...` for another bridge, `?arg=--foo`
for any client argument.

## What works

| | Chrome | Firefox |
|---|---|---|
| Login screen | yes (57 s from a cold start, headless) | yes (235 s, headless) |
| Log in, relay, create a character | yes | not yet run |
| In the world: walk, sound, backpack, drag and drop | yes | not yet run |

The client reads about 230 MiB of the install to reach the login screen and
about 370 MiB for a short session, out of 2.6 GB.

## Known limits

- **Local only for now.** The page, your install and the bridge all run on
  your PC. A hosted page needs a static host and a public `wss://` bridge
  behind TLS; nothing is set up.
- **An unsigned community engine** exports it. It is used on the owner's PC;
  whether CI may use it is undecided (see the proposal in
  `docs/web/unblock-report.md`).
- **The page must be cross-origin isolated** (the engine is threaded).
  `serve` sends the headers; most static hosts do not.
- **Big download**: about 67 MB of wasm and 68 MB of pack, uncompressed.
- **Slow first start** (about a minute in Chrome), much of it the .NET
  runtime starting in WebAssembly.
- **No server latency** in the server list: browsers cannot ping.
- **Sound needs a click** in a real browser: pages may not play audio before
  the first user gesture.
- **No plugins** (Razor and the like are Windows DLLs).
- Picking your UO folder in the browser, instead of running `serve`, is
  designed but not built.
