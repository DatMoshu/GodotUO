# Web boot: debug vs release, and why Firefox is slow

2026-09-28, headless Chrome and Firefox (Playwright's), the web export from
the fork in `tools/godot_web` (ADR-0008, Amendment 1), 35 GB free, nothing
else heavy running. Time from page load to `[GUO] login probe: ok`.

| Run | Chrome | Firefox |
|---|---:|---:|
| debug, install served over HTTP | 56.9 s | 227.5 s |
| release, install served over HTTP | 58.1 s | 212.8 s |
| debug, folder picked in the page (2026-09-27) | 56.3 s | not reached in 282 s |
| release, folder picked in the page | 51.8 s | 212.7 s |
| release, served, Firefox pref `javascript.options.wasm_baselinejit=false` | | not started in 600 s |

Every run read the same 250 range requests, 227 MiB.

**Release does not speed up the boot.** `export --release` works (the fork
ships `web_release.zip`) and makes the wasm 56 MB against 67 MB, but boot
time moves by at most 7% either way. The debug template was not the cause of
Firefox's slowness, so the default stays `--export-debug` for now.

**Firefox's time is in running the wasm, not in reading files.** The per-file
gaps in the client's trace are 4-6 s in Firefox for every archive, even a
one-chunk file such as `skills.mul`, against well under a second in Chrome,
and a picked folder (no HTTP at all) is exactly as slow as a served one. The
C# runs in .NET's interpreter inside the 56-67 MB module. With Firefox's
baseline wasm compiler switched off, the page did not get past loading in
600 s: its optimising tier cannot finish a module this size inside the boot,
so the whole boot runs on baseline code. That fits a steady 3.5-4x slowdown.

**What would help** (not done; each is its own item):
- a smaller module: trimming the .NET assemblies and the engine, so the
  optimising tier finishes sooner;
- AOT-compiling the C# to wasm (the B3 experiment), so the hot code is not
  interpreted;
- nothing on the file side: the reads are the same in both browsers.

With a picked folder Firefox now reaches the login screen (212.7 s given a
900 s timeout); the earlier "not reached" was the timeout.

One tool pitfall found on the way: with `UO_WEB_GODOT` unset, the default
points at this checkout's own `tools/godot_web`, and a worktree without the
fork there falls back to the official Godot, which refuses the export and
leaves `build\web` without its wasm. `doctor` reports the missing fork.
