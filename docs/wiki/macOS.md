# macOS

Developing and playing on a Mac, from the same clone, with the `.sh`
launchers described in [Linux](Linux.md): the same `config.local.sh`, the same
`common.sh`, the same commands. This page lists only what differs on macOS.

## Requirements

| | |
|---|---|
| **macOS** | Apple Silicon (arm64). Checked on macOS 26 (Darwin 25). Intel Macs take the same universal engine build; untested. |
| **Godot** | 4.7.2 stable, **mono/.NET**, `mono_macos.universal`. `launchers/dev/fetch_godot.sh` fetches it into `tools/godot/Godot_v4.7.2-stable_mono_macos.universal/Godot_mono.app`; the launchers run the binary inside the bundle, `Contents/MacOS/Godot`. |
| **.NET SDK** | **10.0**, as on Linux. The client itself runs on the engine's .NET; the **8.0 runtime** is still needed by the tools that run as their own `net8.0` processes (the MapGen CLI, the Store's shard-content installer). Without it those editor checks exit 150. |
| **Python** | 3.12 or newer, as `python3` (Homebrew's is fine). macOS has no `python`; the editor falls back to `python3` when `UO_PYTHON` is not set. |
| **A UO client install** | The Windows install works as it is, including one inside a CrossOver or Wine bottle (`.../Bottles/<name>/drive_c/...`). |

macOS ships bash 3.2 as `/bin/bash`; the launchers keep to it.

## From a fresh clone

```sh
cp launchers/_shared/config.local.sh.example launchers/_shared/config.local.sh
# set UO_CLIENT_DATA and UO_CLIENT_VERSION in it; an external UO_BUILD_DIR is recommended
launchers/pipeline/00_bootstrap.sh       # engine, upstream reference, data check
launchers/dev/smoke.sh                   # the seven-step health check
launchers/game/play.sh                   # run it
```

## What differs from Linux

- **`cuoapi.dll` on arm64.** Upstream ships the plugin ABI assembly IL-only
  but stamped PE32+/AMD64, which an arm64 .NET process refuses. On a
  non-Windows arm64 host `tools/msbuild/CuoApiAnyCpu.targets` makes the build
  reference a copy of the same file with its COFF Machine field set to I386,
  which the loader reads as AnyCPU. Metadata and IL are unchanged. Linux
  arm64 takes the same path. Windows never does.
- **`/bin/sh` is a shim** that execs `/bin/bash`, so the server-console
  wrapper the editor records (`ManagedServerProcess`) runs `/bin/bash`
  directly on macOS; otherwise the recorded image no longer matches the
  running process.
- **`/var` and `/tmp` are links** into `/private`, and every temp folder
  lives below one. The Store's no-links rule lets exactly those top-level
  system aliases through on macOS.

## Performance

The client starts the world on the **canvas** renderer on macOS; the startup
log says `canvas (macOS default; --depth-world for Depth World)`. Depth World,
the default elsewhere, is still there with `--depth-world`.
Options that need Depth World, such as the lighting speed mode, say
"Requires the Depth World" on macOS unless the client runs with
`--depth-world`; the smoke's effect-policy and options-flow probes pass it.

Measured with `tools/perf_probe` on an Apple M5 (Metal, Forward+), window
2940x1658 (a 1280x720 request on a Retina screen), 360 frames per scene,
against a local ServUO-based shard:

| World renderer | open field p50 / p95 ms | Britain bank p50 / p95 ms |
|---|---:|---:|
| Depth World, Debug build (what `play.sh` ran) | 16.5 / 33.1 | 21.3 / 36.9 |
| Depth World, optimised build | 15.6 / 31.9 | 19.5 / 34.7 |
| Depth World, `--rendering-method mobile` | 15.9 / 32.9 | 19.7 / 35.2 |
| **Canvas** (`--canvas-world`) | **16.7 / 17.2** | **16.7 / 17.1** |
| Canvas under `gl_compatibility` | 10.0 / 15.1 | 8.0 / 13.7 |

Depth World's frames overrun the display interval on Metal, so many take
two (p95 at twice the median: visible stutter), and at the bank the median
itself drops below 60 FPS. The managed side is not the cost: world prepare
and draw are about 0.3 ms per frame, and an optimised build gains under 10%.
Canvas holds the display rate. Metal kept presenting at the display rate
although the probe turns vsync off; `gl_compatibility` did not, which is why
its frame times are lower. It draws the same canvas path (Compatibility
always selects canvas), so in normal play, where the client caps the frame
rate, it gains nothing, while it gives up Forward+ for the whole project,
editor included, on a graphics API Apple deprecated. So the default is
canvas on Metal, not OpenGL.

Why Depth World overruns on Metal is not yet known: GPU time was not
captured (the probe reports 0 there).

## Where things go

As on Linux: the cache and the client home default to
`~/.local/share/GUO` unless `XDG_DATA_HOME` or the settings say otherwise.

## What is checked

On an Apple Silicon Mac with a 7.0.95.0 install in a CrossOver bottle:
the bootstrap fetches the engine and upstream, `smoke.sh` passes engine,
import, C# build, client data, the offline load and the launcher lint, plus
the synthetic contracts (audio and HD packs, diagnostics, selection seam,
UO+ options). Against a local shard, `screenshot.sh --play --autologin`
logs in, lists the shard and reaches character creation, and the perf probe
plays in the world (above).

Not yet checked: an export, audio on a real output device, plugins (the
assistants upstream loads are Windows DLLs), and a long in-world play
session. In the editor smoke, the checks that need the .NET 8 runtime fail
without it, and `editor_stage_mapping_and_bytes_write_refused`
fails because `FileShare` is advisory on Unix .NET, as on Linux.
