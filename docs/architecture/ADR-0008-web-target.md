# ADR-0008: Web Target

## Status

Proposed — blocked upstream. On 2026-09-26 the pinned engine refused the
export on this machine with the exact text quoted under Validation. The
tooling, the preset, the server and the smoke are written and the parts that
do not need an export (doctor, serve) are run; nothing has rendered a frame
in a browser. This ADR moves to Accepted when `launchers\web\smoke.bat`
exits 0 on a Godot build that can export C# to the web.

## Date

2026-09-26

## Last Verified

2026-09-26 — against Godot 4.7.2 stable mono, its mono export templates,
and .NET SDK 10.0.301 on Windows 11.

## Decision Makers

Project owner; `mobile-web-engineer` (the tool, the preset, the smoke);
`uo-port-strategist` (whether a third platform is worth carrying).

## Summary

GUO cannot be exported to the web with the pinned engine, because Godot 4.x
does not support C#/.NET on its web platform at all: the mono editor refuses
the export before it starts, and the mono export templates ship no web
template. The decision is to keep the web target as a fully written,
config-driven pipeline (`tools/web`, `launchers/web`, a tracked preset
template, a server that sends the cross-origin isolation headers, a headless
browser smoke) whose `doctor` says exactly why it cannot run today, and to
record here what the client-data path will have to be when it can — rather
than to rewrite the client in another language or fork the engine.

## Context — measured, not assumed

### What a Godot 4.7.2 .NET web export is

- **There is none.** `modules/mono/editor/GodotTools/GodotTools/Export/
  ExportPlugin.cs` refuses `web` with a fixed message (quoted under
  Validation). The tracking issue is godotengine/godot#70796, open since
  4.0: the .NET runtime Godot 4 embeds (CoreCLR/Mono via the hosting API)
  has no Emscripten target the engine can link against. Godot 3.x's Mono
  build could export to the web; Godot 4.x's cannot, and 4.7 changed nothing
  here.
- **The templates say the same.** `%APPDATA%\Godot\export_templates\
  4.7.2.stable.mono` unpacked from `Godot_v4.7.2-stable_mono_export_templates
  .tpz` holds 27 files: android, ios, linux, macos and windows templates,
  and **no `web_*.zip`**. The non-mono templates ship `web_debug.zip`,
  `web_release.zip`, `web_dlink_*` and `web_nothreads_*`; none of them can
  carry a C# assembly.
- **The .NET side exists but has nowhere to go.** `dotnet workload search
  wasm` lists `wasm-tools` and `wasm-experimental` (Blazor/Uno style
  browser-wasm). They produce a `browser-wasm` runtime that Godot's web
  template does not host. On this machine `dotnet workload list` itself
  fails (exit 1 after printing the manifest version; a known COM
  registration error, 0x8007007E), so `doctor` reports what it saw rather
  than guessing.
- **The two-runtime shape of the game.** The ported client is a
  single-threaded loop over blocking file reads (`MemoryMappedFile` over
  multi-GB `.mul`/`.uop`) and a blocking TCP socket. A browser has neither
  mmap nor raw TCP. So even once the engine can host C# on the web, GUO
  needs two more pieces before it can play: a data path and a socket path.

### What the page would need from its server

Godot's threaded web template uses `SharedArrayBuffer`, which browsers only
enable on a **cross-origin isolated** page: the response must carry
`Cross-Origin-Opener-Policy: same-origin` and
`Cross-Origin-Embedder-Policy: require-corp`. GitHub Pages, S3 and most
static hosts do not send them; Godot's own `progressive_web_app/
ensure_cross_origin_isolation_headers` installs a service worker that
re-serves the page with them (`coi-serviceworker`), which is what the preset
template turns on. For local runs, `tools/web/run.py serve` sends the
headers itself.

### Where the client data can come from in a browser

The proprietary install (rule 8) can never be part of the page. The options,
measured against what the client does with the files:

| Path | mmap / seek | Size limit | Persists | Notes |
|---|---|---|---|---|
| **File System Access API** (`showDirectoryPicker`) | no mmap; `File.slice()` + `arrayBuffer()` is a seek | none | permission per session (Chromium); no Firefox/Safari | the user picks their UO folder; nothing leaves the machine |
| **Local range-request server** (`python tools/web/run.py serve --data <UO folder>`) | HTTP `Range` = seek | none | as long as the server runs | works in every browser; the data is served from the user's own PC; same origin as the page so no CORS |
| Origin Private File System (OPFS) | `FileSystemSyncAccessHandle` is a real seek, callable from a worker | quota (GBs on desktop) | yes | needs an import step from one of the above; the only path that gives Godot's `FileAccess` something synchronous |
| IndexedDB (Godot's default persistent `user://`) | no | quota; slow for GBs | yes | not a fit for 2+ GB of art |
| Bundled in the PCK | n/a | 2 GB Emscripten limit, and rule 8 | n/a | never |

Every one of those meets the client through **`IO/`**, whose readers open
files by path and hand out `MemoryMappedViewAccessor`s / `Span`s
(`UOFileMul`, `UOFileUop`, `DataReader`). The design, when it becomes
possible, is a `UOFile` backend behind the same reader API: an `IUOFile`
that the existing mmap class implements on desktop and a **chunked
`Range`-request or OPFS reader** implements on the web, with the loaders
above it unchanged. Godot's own `FileAccess` on the web is synchronous over
its in-memory/IDBFS filesystem and cannot back a 2 GB map, so the backend
must be the client's, not the engine's.

The socket has the same shape: `Network/NetClient` opens a `Socket`; the
web needs a WebSocket-to-TCP relay next to the shard (or a shard that speaks
WebSocket) and a `NetClient` backend over `ClientWebSocket`/Godot's
`WebSocketPeer`. Out of scope for this ADR, but it is the second blocker and
is recorded so nobody expects a login from the first successful export.

## Decision

### 1. Keep a config-driven web pipeline, parallel to Android

`tools/web/run.py` with `doctor`, `preset`, `export`, `serve` and `smoke`,
behind `launchers/web/{doctor,export,serve,smoke}.bat`, mirroring
`tools/android`. The preset is rendered from
`tools/web/export_presets.template.cfg` into the gitignored
`godot/GUO/export_presets.cfg`, the same file the Android tool renders; each
tool renders before it exports, so the two never need to coexist. One new
setting, `UO_WEB_PORT` (default 8060), in `config.bat` and `guo.config`.

`doctor` checks the templates folder for `web_dlink_debug.zip` /
`web_debug.zip`, reports the wasm workload state, finds a Chromium binary
(`UO_WEB_BROWSER` overrides), and fails with the reason. `export` runs
`godot-console --headless --export-debug Web build\web\GUO.html` and puts
the refusal in `build\web\export.log`. Both are cheap and honest, and the
day the templates arrive the pipeline is one `templates` step away.

### 2. The page is served cross-origin isolated

`serve` is a threaded `http.server` on `127.0.0.1:UO_WEB_PORT` that adds
`Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Embedder-Policy:
require-corp`, `Cross-Origin-Resource-Policy: same-origin` and
`Cache-Control: no-store`, and serves `.wasm` as `application/wasm`. The
preset keeps `variant/thread_support=true` (the client blocks; the
no-threads template cannot) and `progressive_web_app/
ensure_cross_origin_isolation_headers=true` for hosts that cannot send the
headers.

### 3. The smoke watches the browser console

`smoke` exports, serves, launches Chrome/Edge `--headless=new
--enable-logging=stderr --v=1 --screenshot` at the page, and passes when a
console line carries `[GUO]` (the client's own log prefix, the same one the
Android smoke waits for on logcat) or the `Godot Engine v` banner, with no
`SharedArrayBuffer`/`Uncaught` error. Output: `build\web\smoke_console.txt`
and `build\web\smoke.png`. Playwright is not required; a plain Chromium is.

### 4. Client data: designed, not built

The `IUOFile` backend split above is the design. It is not implemented,
because there is nothing to run it in: no export, so no page, so no way to
verify a single byte read. Building a Range-request reader blind against
`sources/`-shaped loaders would be a port deviation with no smoke, which
rule 6 forbids calling done. `serve` does not yet take a `--data` folder for
the same reason.

### 5. Out of scope

A WebSocket relay for the shard; a hosted deployment; iOS Safari; a Godot
fork or a GDScript/C++ rewrite of the client for the web.

## Alternatives Rejected

- **Godot 3.x mono, which can export C# to the web.** A different engine
  API for the whole renderer (ADR-0001 to ADR-0006 are 4.x), for a target
  that would still lack mmap and TCP.
- **A non-mono Godot 4 web export with the game in GDScript/C++.** That is
  a rewrite of 150,000 lines of C#, which is what this project exists not to
  do.
- **Blazor-style `browser-wasm` with a Godot-less renderer.** Same rewrite,
  different corner.
- **Silently leaving the web target out.** The owner asked for the finding
  to be recorded and the pipeline to exist; a `doctor` that names the
  blocker is worth more than a missing folder.

## Consequences

- No behaviour change anywhere: `tools/web` and `launchers/web` are new;
  `config.bat` and `guo.config` gain one key; the Android preset template
  gains the same `exclude_filter="addons/guo_editor/*"` as the web one, so
  neither export packs the editor add-on's `.cs` files as text resources.
- CI can run `python tools/web/run.py doctor` and expect exit 1 with the
  templates line as the only engine-side MISS; it cannot run the export.
- When Godot ships C# on the web, the work is: templates, the `IUOFile`
  backend, and a socket backend, in that order, each with its own smoke.

## Validation

Recorded 2026-09-26 on Windows 11, Godot 4.7.2 stable mono (pinned in
`tools/godot`), .NET SDK 10.0.301, Chrome at
`C:\Program Files\Google\Chrome\Application\chrome.exe`.

1. `python tools\web\run.py doctor` — exit 1:

   ```
   ok   dotnet SDK                   C:\Program Files\dotnet\dotnet.EXE
   ok   Godot console (mono)         ...\Godot_v4.7.2-stable_mono_win64_console.exe
   MISS Web export templates         none of web_dlink_debug.zip, web_debug.zip in ...\export_templates\4.7.2.stable.mono
        fix: none exist for a mono build of Godot 4.x: the .NET web export is unsupported upstream (godotengine/godot#70796). See ADR-0008.
        templates folder holds 27 files, 0 of them web_*
   MISS dotnet wasm workload         dotnet workload list exited 1: Workload version: 10.0.300-manifests.8c7d7c03
        fix: dotnet workload install wasm-tools   (only useful once the engine can export)
   ok   preset template              ...\tools\web\export_presets.template.cfg
   ok   serve port                   UO_WEB_PORT=8060
   ok   headless browser             C:\Program Files\Google\Chrome\Application\chrome.exe
   ```

2. `python tools\web\run.py export` — exit 1; `build\web\export.log` holds
   the engine's refusal, verbatim:

   ```
   ERROR: Cannot export project with preset "Web" due to configuration errors:
   Exporting to Web is currently not supported in Godot 4 when using C#/.NET. Use Godot 3 to target Web with C#/Mono instead.
   If this project does not use C#, use a non-C# editor build to export the project.
      at: _fs_changed (editor/editor_node.cpp:1401)
   ERROR: Project export for preset "Web" failed.
   ```

3. `python tools\web\run.py serve --root <folder with a test GUO.html>` then
   `curl -I http://127.0.0.1:8060/GUO.html` — `200 OK` with
   `Cross-Origin-Opener-Policy: same-origin`,
   `Cross-Origin-Embedder-Policy: require-corp`,
   `Cross-Origin-Resource-Policy: same-origin`, `Cache-Control: no-store`.

4. `python tools\web\run.py smoke` — exit 1 at the export step, by design;
   the browser half has not run against a real page and is **written, not
   verified**.

## Related

- ADR-0007 (Android target): the tool this one mirrors, and the touch layer
  a web build on a phone would reuse.
- ADR-0001 (render presenter seam): why the client owns its window and scale.
- `tools/web/README.md` — the how-to and the same run table.
- godotengine/godot#70796 — "Web export with C#/.NET".
