# Linux

Developing and playing on a Linux desktop, from the same clone. Every
launcher the desktop needs has a `.sh` twin beside its `.bat`; each one
sources `launchers/_shared/common.sh`, which resolves the
[Configuration](Configuration.md) and locates the engine.

Settings are still written in `launchers/_shared/config.local.bat`, on Linux
too. `common.sh` does not run it: `tools/shellenv` reads it through
`tools/guo/config.py`, in the usual order (environment, `config.local.bat`,
`config.bat`), and exports the result. One file of settings serves every OS.

The [Steam Deck](Steam-Deck.md) page is a different job: it exports a
standalone build and pushes it to a Deck over ssh.

## Requirements

| | |
|---|---|
| **Linux** | x86_64 (arm64 should work; untested). Checked on Linux Mint 22 (Ubuntu 24.04 base), X11. |
| **Godot** | 4.7.2 stable, **mono/.NET**, `mono_linux_x86_64`. `launchers/dev/fetch_godot.sh` fetches it into `tools/godot`. |
| **.NET SDK** | **10.0.** The project's C# uses C# 14 features (the `field` keyword, first-class spans), so an 8.0 or 9.0 SDK fails the build even though the client targets `net8.0`. The 8.0 *runtime* must be installed beside it, because the client runs on `net8.0`. |
| **Python** | 3.12 or newer, as `python3`. |
| **Tools** | `git`, `curl`, `unzip`. `xvfb-run` for scripted runs that should not open a window on your desktop. |
| **A UO client install** | The Windows install works as it is, including one inside a Wine prefix. |

A per-user .NET with no root, if the distribution's packages are older:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet
bash dotnet-install.sh --channel 8.0 --runtime dotnet --install-dir ~/.dotnet
export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH
```

`common.sh` uses `~/.dotnet` by itself when no `dotnet` is on `PATH`.

## From a fresh clone

```sh
cp launchers/_shared/config.local.bat.example launchers/_shared/config.local.bat
# set UO_CLIENT_DATA (and UO_CLIENT_VERSION) in it, with forward slashes
launchers/pipeline/00_bootstrap.sh       # engine, upstream reference, data check
launchers/dev/smoke.sh                   # the six-step health check
launchers/game/play.sh                   # run it
```

A UO install in a Wine prefix is typically at
`~/.wine/drive_c/Program Files (x86)/Electronic Arts/Ultima Online Classic`.
Set `UO_CLIENT_VERSION` to that client's version: the dev shard reads it from
the same install and kicks an older version than the one it finds.

## The dev shard

```sh
launchers/shard/fetch.sh       # clone ModernUO + apply tools/modernuo/patches
launchers/shard/build.sh       # publish release linux x64
launchers/shard/run.sh         # run it (Ctrl-C to stop)
launchers/shard/populate.sh    # generate the world (once)
```

ModernUO publishes a native `ModernUO` apphost on Linux; `tools/editor_shard`
uses it for the editor's private instance as well.

## Where things go

| | |
|---|---|
| Cache (`UO_CACHE_DIR`) | `~/.local/share/GUO/cache` (`$XDG_DATA_HOME` if set) |
| Client home: profiles, journal logs, screenshots | `~/.local/share/GUO`, the cache's parent, as on Windows |

## What is checked

On Linux Mint 22 with a 7.0.114.2 install in a Wine prefix: `smoke.sh` passes
all six steps, the editor smoke passes all eighteen checks, the dev shard
builds, boots headless and is populated from the client, and the input probe
logs in, walks the world, opens gumps and gets speech back from the shard.

Not yet checked: audio on a real output device, the GUO Asset Store, plugins
(the assistant plugins upstream loads are Windows DLLs), the web and Android
exports from a Linux host.

## Scripted runs without a window

Godot opens its window on your desktop. For probes and screenshots that
should not take the screen, run the engine under a virtual display:

```sh
GODOT_CONSOLE=~/bin/xvfb-godot launchers/dev/playtest.sh
```

where `~/bin/xvfb-godot` is a two-line wrapper of your own, pointing at your
clone's engine:

```sh
#!/bin/sh
exec xvfb-run -a -s "-screen 0 1920x1080x24" /path/to/GUO/tools/godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64 --audio-driver Dummy "$@"
```

`GODOT_CONSOLE` in the environment wins over the pinned engine, as every
setting does.
