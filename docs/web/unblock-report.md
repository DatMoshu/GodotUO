# GUO on the web: what blocks it, and how to unblock it

Date: 2026-09-27. Branch: `work/web-unblock`. Author: GUOWeb (story S10).
Governs nothing by itself; ADR-0008 stays the decision record, and an
amendment follows once phase 2 has measured something.

Everything below marked **measured** was read from GitHub, the repo or this
machine today. Everything marked **claimed** is what an upstream author wrote
and nobody here has run yet.

## The short answer

The engine blocker that ADR-0008 recorded on 2026-09-26 is no longer the
wall it looked like. Godot has not merged C# web export, but **two working
upstream branches exist, and one of them ships a prebuilt Windows editor plus
web templates for exactly our pin, 4.7.2-stable**. So the first proof (a C#
project rendering in Chromium) is hours, not weeks, and needs no emscripten
build on this machine.

What is left after that is GUO's own work, and it is ordinary port work
behind existing seams: the client data (2.6 GB) has to reach the browser's
virtual filesystem, the shard socket has to become a WebSocket (upstream
ClassicUO already has a WebSocket client path; ModernUO has no server side,
so we need a small proxy), and the Windows-only corners (plugins, UoAssist,
native zlib) have to be switched off for the web the same way they already
are for Android.

A *deployable* web client (a public URL anyone opens) additionally needs an
owner decision about where the client data comes from, because we can never
host it (rule 8).

## Upstream state, measured

| | #106125 (raulsntos) | #118976 (NoctemCat) | ComplexRobot fork |
|---|---|---|---|
| What | Mono browser-wasm runtime statically linked into Godot's wasm via the embedding API | Godot built as a static **LibGodot** and linked into a normal `dotnet publish` browser-wasm app | #106125 merged onto stable tags, plus a JS fix so exports need no hand edits |
| Author | Godot's C# maintainer | community | community (51 stars) |
| State | draft, `REVIEW_REQUIRED`, milestone 4.x, open since 2025-05-06, last activity 2026-09-14 | draft, `REVIEW_REQUIRED`, milestone 4.x, opened 2026-04-26, last activity 2026-07-03 | releases for 4.7.1, **4.7.2-stable** (2026-08-18) and 4.8-dev1..dev6 |
| Size | +587 / −36, 15 files, one commit, ~12k commits behind master | +1619 / −151, 51 files | branch tip 68 ahead / 62 behind `4.7.2-stable` |
| .NET | net9.0, Mono interpreter (no AOT) | net9 and net10, interpreter or `RunAOTCompilation` | net9.0 (project must target it) |
| Emscripten | Godot's own | pinned to **3.1.56** (the version .NET's workload freezes; dotnet/runtime#113786) | Godot's own, prebuilt |
| Threads | threaded Mono runtime required by the demo (`WasmEnableThreads`) | single- and multi-threaded both work (claimed) | threaded (needs SharedArrayBuffer) |
| Known gaps | globalization forced to invariant; JS-side BCL stubs, so crypto etc. fail; a ~60 MB runtime (claimed, #118976 thread) | GDExtension unsupported; no LTO (engine ~60 MB instead of ~40); slow export (links all of Godot); `System.Net.WebSockets` unreliable, Godot's `WebSocketPeer` fine (claimed by a tester) | GDExtension unsupported; open issues: WebGL `ArrayBufferView not big enough` black screen on **4.7.1 + Chrome** (not Firefox; 4.6.1 fine), `GD.Randf()` crash, export errors in one user's setup |
| Prebuilt | no (a demo at lab.godotengine.org) | no (itch.io demos only) | **yes**: `Godot_v4.7.2-stable_mono_web_export_win64.zip`, 167 MB, editor + `web_debug.zip`/`web_release.zip` + a local NuGet feed |
| Mergeable? | not soon: draft for 16 months, the maintainer has not rebased, the PR thread is flooded with AI-generated "help" and reviewers are pushing back on it | not soon: draft, and it forces emscripten 3.1.56 on the whole web platform, which Godot will not accept as is | n/a, a fork |

Other branches seen: `jlucaso1/godot#1` (same architecture as #106125,
emscripten 4.0.11, single-threaded, fixes globalization; explicitly not
submitted upstream) and several helper PRs against the two forks. None has a
prebuilt artifact.

The .NET side (dotnet/runtime): browser-wasm is supported and ships in the
`wasm-tools` workload. **Multithreading is still experimental** in .NET 10
and 11 (`WasmEnableThreads`; tracking dotnet/runtime#68162), with open bugs
such as timers throwing `SynchronizationLockException` on net10 and not
firing on net11 (dotnet/runtime#133984), and the "deputy thread" model runs
C#'s main thread off the browser's main thread. CoreCLR-on-wasm is future
work (dotnet/runtime#121511). What Godot needs from .NET is exactly what
both PRs work around: a runtime it can link statically, a P/Invoke table
that covers Godot's interop, and the JS half of the runtime.

**Realistic upstream timeline: not in 4.8.** Nothing here should wait on
Godot merging a PR. We build on a fork, and re-evaluate on each Godot
release.

## GUO's own blockers, measured in `godot/GUO/src`

171,682 lines of C#. The platform-sensitive sites:

| What | Where | On the web |
|---|---|---|
| File reads | every loader opens paths with `File.Exists`/`File.Open`; `UOFile` → `MMFileReader` maps the whole file | .NET's `FileStream` goes to emscripten's virtual FS, so **paths keep working if the files are in that FS**. `MemoryMappedFile` over it is unverified on browser-wasm; if it throws, a stream-backed `FileReader` is a one-file deviation (the `FileReader` base already reads through a `BinaryReader`). |
| TCP to the shard | `Network/NetClient` → `TcpSocketWrapper` (non-blocking, polled; no receive thread) | browsers cannot open TCP. **Upstream already switches to `WebSocketWrapper` when the address starts with `ws`/`wss`.** That wrapper builds its own `TcpSocket` inside a `SocketsHttpHandler`, which browser-wasm does not have: a small `// PORT DEVIATION (GUO):` to use plain `ClientWebSocket` on the web (or Godot's `WebSocketPeer`, reported more reliable). |
| A WebSocket server | none: ModernUO speaks raw TCP only | a TCP↔WS proxy next to the shard (`tools/ws_bridge/`, ~150 lines of Python `websockets`/asyncio, or `websockify`). Each binary WS message carries raw UO bytes; encryption/compression are unchanged end to end. |
| Threads | `Task.Run` in `WorldMapGump`, `UltimaLive`, `Utility/Extensions`; blocking waits only in `UltimaLive` and `WebSocketWrapper` | the game loop itself is single-threaded and the socket is polled, so **a no-threads build is plausible** and would avoid SharedArrayBuffer and experimental .NET threading. `Task.Run` then runs inline on the one thread; the two blocking waits must not be hit on the web. |
| P/Invoke | `UoAssist` (user32/kernel32), `Utility/Platforms/Native` (kernel32), `Network/Plugin` + `PluginClrHost` (mscoree, .NET Framework), `Utility/ZLib` (`zlib`/`libz`) | all Windows or native: disabled on the web the way `GuoPluginHost` is already gated off for Android. ZLib needs its managed fallback path (to check). |
| Renderer | `rendering_method="forward_plus"` | the web template only has Compatibility (WebGL 2). Android already exercises a GL path; the web adds the ComplexRobot #16 WebGL risk above. |
| Target framework | `net8.0`, `net9.0` for Android | the fork needs `net9.0`: one more condition next to the Android one, plus a `Program.cs` the fork requires. |

## Blockers, ranked

| # | Blocker | How to unblock | Who acts | Effort |
|---|---|---|---|---|
| 1 | **No engine that exports C# to the web** (our pin refuses) | Use the ComplexRobot 4.7.2 prebuilt in `tools/godot_web/`, self-contained (`._sc_`), with its templates in its own `editor_data/` and its NuGet feed in a project-local source. **Never run its `install.bat`**: it writes web templates into the same `%APPDATA%` template folder our pinned engine uses and puts a rebuilt `Godot.NET.Sdk 4.7.2` into the user NuGet feed, where it would shadow the official one. Fallback if its WebGL bug bites: build #118976 or #106125 from source. | us | 1–3 h to a hello-world in headless Chromium |
| 2 | **Trusting a third-party binary** | The owner accepts running a community-built, unsigned Godot editor on this PC and in CI, or we build the same branch from source (see "Building from source"). | **owner** | a decision |
| 3 | **Client data in the browser** (2.6 GB install, can never be hosted by us) | Phase 2 for a dev loop: `serve --data` streams the user's own install from localhost, and the page fetches the files the login screen needs into the virtual FS before `main`. For playing: lazy loading of the big files (sync HTTP `Range` reads from a worker, or the user picks their UO folder with the File System Access API and we read slices). wasm32's 4 GB address space rules out loading all of it. | us; the owner picks the deployment model (below) | login subset: half a day. whole-game lazy FS: 2–5 days |
| 4 | **No TCP in a browser** | `tools/ws_bridge/` proxy next to the shard; GUO connects to `ws://host:port`; one deviation in `WebSocketWrapper` | us | half a day |
| 5 | **Windows-only code** (plugins, UoAssist, kernel32, native zlib) | Compile-time or runtime gates for `browser`, as done for Android | us | half a day |
| 6 | **WebGL on 4.7.x** (ComplexRobot #16: black screen on Chrome, fine on Firefox and on 4.6.1) | Measure first. If hit: try Firefox, or the fork's 4.8-dev build. Dropping to 4.6 is not an option (our pin). This could force waiting for a fix. | us to measure; upstream to fix | unknown until run |
| 7 | **Threads vs. SharedArrayBuffer** | Prefer a no-threads build (GUO does not need threads; see above); that also lifts the COOP/COEP requirement on hosts like GitHub Pages. The prebuilt is threaded, so this needs #118976 built from source. Threaded is fine for the local proof: `serve` already sends COOP/COEP. | us | only if a no-threads build is wanted: a source build (below) |
| 8 | **Payload size** | GUO.dll is 5.4 MB; expect the engine ~40–60 MB wasm, the .NET runtime and BCL 10–60 MB depending on branch and trimming. Trim, Brotli on the host (typically ~4× on wasm). A few tens of MB compressed on first load, cached after. | us | measure in phase 2 |
| 9 | **Hosting** | A static host for the page (GitHub Pages works with the service-worker COOP/COEP shim, or with a no-threads build), and a public `wss://` endpoint for the proxy with TLS if the page is served over https. | **owner** (where, which domain, which shard) | a decision, then an hour |
| 10 | **Upstream merge** | Nothing to do but follow #106125/#118976 and rebase our tool onto whatever lands. | upstream | not in 4.8 |

### The owner's decision: where the client data comes from

A public web client cannot ship UO's art. The honest options:

1. **Local only**: the player runs `serve` on their own PC with their own
   install; the page is at `http://127.0.0.1`. Works in every browser, no
   legal question, no hosting. Good for dev and for us; not a "web client"
   anyone can just open.
2. **Hosted page, player's own files**: the page is public, the player
   points it at their UO folder (File System Access API in Chromium; a
   folder `<input>` elsewhere) and the files stay on their machine. The
   right model for a public deployment. Most work (lazy reads from `File`
   slices in a worker).
3. **A shard hosts its own data**: a shard operator serves client files
   they are licensed to serve next to their `wss://` endpoint. Not ours to
   do; our loader would support it for free once option 2's lazy FS exists.

Recommendation: build 1 now (it is phase 2), design the FS layer so 2
and 3 are the same code with a different byte source.

## Building from source instead (if the prebuilt is refused or broken)

For #118976 (the one that can build no-threads and AOT), on this PC:

- Tools: Python + SCons (have Python 3.12), emsdk 3.1.56 (~1 GB), .NET 9/10
  SDKs (have both) + `wasm-tools` workload (~1–2 GB), a Godot source
  checkout (~1.5 GB with objects).
- Steps: build the mono editor (`scons target=editor module_mono_enabled=yes`,
  about 30–60 min on a desktop CPU), generate glue, `build_assemblies.py`,
  then the web template with emscripten (another 30–60 min per variant).
- Disk: plan for **15–20 GB** in `tools/godot_web/` with object files. C:
  has 82 GB free and D: 70 GB free, enough but not generous.
- Known snags: `dotnet workload list` already fails on this machine (COM
  0x8007007E, ADR-0008); on Windows the emscripten cache must be on the
  same drive as the .NET install (C:), per #118976's thread.
- Total: **half a day to a day**, most of it compile time.

## Recommended path

1. **Now, cheap, no owner risk beyond #2:** fetch the ComplexRobot 4.7.2
   zip into `tools/godot_web/` (gitignored, README with origin and hash),
   run it self-contained, export a ten-line C# project, and prove it prints
   in headless Chromium through `launchers\web\serve.bat`. This tells us in
   an hour whether the WebGL bug (#6) hits us.
2. Teach `tools/web` a `UO_WEB_GODOT` setting pointing at that editor, add
   the `net9.0`/`Program.cs` web conditions to `GUO.csproj`, gate the
   Windows-only code, and export GUO itself. Target: the login gump
   rendered in Chromium with client data served from `serve --data`.
3. `tools/ws_bridge/` + the `WebSocketWrapper` deviation: log in to the
   local ModernUO through `ws://127.0.0.1:<port>`.
4. Then an ADR-0008 amendment with what was measured, and the owner's
   choices on #2, the data model and hosting.
5. Later: a source build of #118976 for a no-threads, AOT-compiled,
   smaller, reproducible build that CI can make; lazy whole-install FS.

## Proposal: a web export in CI (for the owner to decide; nothing built)

Added 2026-09-27, after the client played in Chrome on this PC. Today the
web export runs only here, as the owner approved. Should GitHub Actions
export it too, and how?

**What CI could prove, and what it could not.** CI has no UO install
(rule 8) and no shard. It could prove that **the web export still builds**,
the most likely thing to rot: a C# change that trips the browser's missing
APIs, or the page patch anchors no longer matching. It could also prove that
the page boots in headless Chrome as far as the client's first log lines
(`?data=none` stops at "No UO client data", after the engine, the .NET
runtime and GUO's bootstrap have all run). It could not prove the login
screen or play; those stay on this PC's `smoke`.

**Three ways to do it:**

| | A. No CI | B. The prebuilt, pinned | C. Build the fork from source |
|---|---|---|---|
| How | the web export stays a local check | a `windows-latest` job downloads the 4.7.2 release zip, **fails unless its SHA-256 matches** the one in `tools/godot_web/README.md`, caches it, installs .NET 10 + `wasm-tools-net9` (a hosted runner has admin rights, unlike this PC), exports, boots the page in Playwright Chrome | a job checks out ComplexRobot/godot at a pinned commit, builds the mono editor and the web template (emsdk, SCons), caches the result by commit |
| Time per run | 0 | ~20–30 min (10 here, and hosted runners are slower; the forced `emcc` relink dominates) | 2–4 h on a cache miss; B's time on a hit |
| Cost | none | free on a public repo; on a private one Windows minutes count double | as B, plus the cache misses; runner disk (~15–20 GB) is tight |
| Trust | none needed | runs a **third-party, unsigned binary** in our CI | we build what we run, from source we can read |
| Provenance | n/a | the zip's hash is pinned, but its origin is one person's GitHub release; the build logs show it was built in their GitHub Actions from the public branch | full: our commit pin, our build log |
| Licence | n/a | Godot and the fork are MIT; running it is fine. **Shipping** its templates in a GUO web release is redistribution: keep Godot's licence notice with it | same |

**Rules any CI option should follow:** a separate workflow, not the main CI.
Manual or weekly triggers, so a fork outage never blocks a merge.
`permissions: contents: read`, no secrets in the job, and no artifact
publishing from it, so an untrusted binary has nothing to steal or sign.
The job should fail, not warn, on a hash mismatch.

**Recommendation.** B now, under those rules. It catches the likely
breakage for a few tens of free minutes a week, and the pinned hash bounds
the trust to a binary the owner has already chosen to run here. Move to C
before GUO publishes web builds for others (going public, Pages), because a
shipped build should come from a source build we can reproduce. Keep A if
the owner would rather no third-party binary run under the repo's name.

## Sources

- godotengine/godot#70796 (the issue), #106125, #118976
- github.com/ComplexRobot/godot-dotnet-web-export (releases, issues 16–18)
- github.com/NoctemCat/DotnetWebExportDemo; github.com/raulsntos/godot4-web-dotnet-prototype
- jlucaso1/godot#1
- dotnet/runtime#68162 (wasm threads), #133984, #113786 (emscripten version), #121511 (CoreCLR on wasm)
