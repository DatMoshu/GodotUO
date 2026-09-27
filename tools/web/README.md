# tools/web -- GUO as a web page

Exports the client for the web, serves it with the headers a threaded Godot
web build needs, and smoke-tests it in a headless browser. The finding that
governs all of it is in
[ADR-0008](../../docs/architecture/ADR-0008-web-target.md):

> **Godot 4.7.2 mono cannot export a C# project to the web.** The engine
> refuses (`Exporting to Web is currently not supported in Godot 4 when using
> C#/.NET`) and its mono export templates ship no web template. Upstream
> tracking issue: godotengine/godot#70796.

This folder exists so that `doctor` says precisely that, and so the pipeline
is ready the day it changes.

```
launchers\web\doctor.bat     what a web export needs, and what is here (exit 1 today)
launchers\web\export.bat     godot-console --headless --export-debug Web build\web\GUO.html
launchers\web\serve.bat      serve build\web on UO_WEB_PORT with COOP/COEP headers
launchers\web\smoke.bat      export + serve + headless Chrome + wait for "[GUO]" in the console
```

Everything is `python tools\web\run.py <command>` underneath; `preset` only
renders the export preset.

## Settings

```
UO_WEB_PORT      default 8060      the local server's port (config.bat / guo.config)
UO_WEB_BROWSER   optional          a Chromium binary for the smoke; Chrome/Edge are found by default
```

## The preset

`export_presets.template.cfg` is rendered into `godot\GUO\export_presets.cfg`
(gitignored; the Android tool renders the same file from its own template,
and each tool re-renders before it exports). What it sets and why:

| Key | Value | Why |
|---|---|---|
| `variant/thread_support` | `true` | the client blocks on file reads and a socket; the no-threads template cannot |
| `progressive_web_app/ensure_cross_origin_isolation_headers` | `true` | a service worker that re-serves the page with COOP/COEP on hosts that cannot send them |
| `html/canvas_resize_policy` | `2` (adaptive) | the client owns its window size; the canvas follows the browser window |
| `exclude_filter` | `addons/guo_editor/*` | the editor add-on's `.cs`/`plugin.cfg` are not game resources |
| `vram_texture_compression/for_desktop` | `true` | required by the web export plugin; the client's art never goes through Godot's importer anyway |

## Cross-origin isolation

The threaded template needs `SharedArrayBuffer`, which a browser only
enables when the page is served with

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

`serve` sends both (plus `Cross-Origin-Resource-Policy: same-origin` and
`Cache-Control: no-store`) and serves `.wasm` as `application/wasm`. Any
other host needs the same two headers, or the preset's service worker.

## The smoke

`smoke` exports, serves `build\web`, and runs Chrome or Edge with
`--headless=new --enable-logging=stderr --v=1 --screenshot=build\web\smoke.png`
at `http://127.0.0.1:<port>/GUO.html`. Every `console.log` line lands in
`build\web\smoke_console.txt`; the page passes once one carries `[GUO]` (the
client's own log prefix, the same one the Android smoke waits for) or the
`Godot Engine v` banner, and fails on a `SharedArrayBuffer`/`Uncaught` line or
the timeout (default 120 s). `--no-export` reuses `build\web`.

## Client data on the web

The UO install can never be part of the page (CLAUDE.md rule 8). ADR-0008
weighs the File System Access API, a local range-request server and OPFS,
and records the design: an `IUOFile` backend behind the existing `IO/`
readers, chunked over HTTP `Range` or OPFS, with the loaders unchanged. It is
not implemented: with no export there is no page to verify a byte read in.

## What has actually been run, and what has only been written

Recorded 2026-09-26, Windows 11, Godot 4.7.2 stable mono, .NET SDK 10.0.301.

| What | Result |
|---|---|
| `python tools\web\run.py doctor` | exit 1: no `web_*` template among the 27 mono template files; `dotnet workload list` itself exits 1 on this PC (COM error 0x8007007E; `dotnet workload search wasm` works); Chrome found |
| `python tools\web\run.py export` | exit 1: `Exporting to Web is currently not supported in Godot 4 when using C#/.NET.` in `build\web\export.log` |
| `python tools\web\run.py serve --root <test folder>` + `curl -I` | `200 OK` with all four headers |
| `python tools\web\run.py smoke` | exit 1 at the export step, as designed; the browser half is **written, not verified** |
| `dotnet build godot\GUO\GUO.csproj` | 0 errors; nothing in the client changed for this folder |
