# Getting Started

From a fresh clone to a running client, on Windows. Every step is a launcher
under `launchers\`; each one calls `launchers\_shared\common.bat` first, which
resolves the [Configuration](Configuration.md) and locates the engine.

On Linux, every launcher here has a `.sh` twin; see [Linux](Linux.md).

Other platforms start from the same clone: [Windows Build](Windows-Build.md)
(a standalone `GUO.exe`), [Android Build](Android-Build.md) (a debug APK) and
[Steam Deck](Steam-Deck.md) (a Linux build pushed over ssh). To play without
building anything, see [Download a build](#download-a-build).

## Requirements

| | |
|---|---|
| **Windows** | 10 or 11, x64 |
| **Godot** | 4.7.2 stable, **mono/.NET** build. Fetched by the bootstrap into `tools\godot` (gitignored). Do not install your own; the version is pinned. |
| **.NET SDK** | 10.0, with the 8.0 runtime. The client targets `net8.0` but is written in C# 14 (`LangVersion` `latest`; the `field` keyword, first-class spans), which only the 10.0 SDK compiles: with an 8.0 SDK alone the build fails in `MessageManager.cs` and `SystemChatControl.cs`. 10.0 also builds the dev shard and the parity tools. (The Android export needs 9.0 as well, see [Android Build](Android-Build.md).) |
| **Python** | 3.12 or newer, on `PATH` as `python` (or set `UO_PYTHON`). |
| **Git** | Any recent version; the bootstrap clones the upstream reference. |
| **A UO client install** | Any modern Classic client. Developed against 7.0.107.76. |

## 1. Clone and bootstrap

```bat
git clone <repository url> GUO
cd GUO
launchers\pipeline\00_bootstrap.bat
```

The bootstrap fetches the pinned engine into `tools\godot` and clones
ClassicUO at the reviewed commit into `sources\ClassicUO` (both gitignored).
The upstream tree is a read-only reference: the client compiles against its
`cuoapi.dll`, and the audit reads its sources. Never edit it.

## 2. Point the project at your UO install

`launchers\_shared\config.bat` holds every setting with its default and is
committed. Your machine's values go in `config.local.bat`, which is
gitignored:

```bat
copy launchers\_shared\config.local.bat.example launchers\_shared\config.local.bat
notepad launchers\_shared\config.local.bat
```

Set `UO_CLIENT_DATA` to the folder holding your `.uop` / `.mul` files. Keep
the `if not defined` guard on every line you add so an environment variable
still wins. Anything in `config.bat` can be overridden here; the resolution
order everywhere is environment variable, then `config.local.bat`, then
`config.bat`, then the optional shared config (`UO_COMMON_CONFIG`).

## 3. Verify the client data

```bat
launchers\pipeline\01_verify_client_data.bat
```

Runs `python tools\uodata\run.py verify`: checks the install is complete
against the known file registry and reports the version it found. Set
`UO_CLIENT_VERSION` in `config.local.bat` if yours differs from the default.

## 4. Build

```bat
dotnet build godot\GUO\GUO.csproj
```

This is the fast loop (about a second when nothing changed) and the one to
use while working. `launchers\dev\build.bat` does the same through the
launcher layer. The full health check is:

```bat
launchers\dev\smoke.bat
```

Five steps: the engine answers, the project imports, the C# builds, the
client data verifies, and the client loads offline. It must pass before a
commit (see [Contributing](Contributing.md)).

## 5. Play

```bat
launchers\game\play.bat
```

The client connects to `UO_SHARD_HOST:UO_SHARD_PORT`, `127.0.0.1:2593` by
default. Nothing answers there until you run a server: see
[Dev Shard](Dev-Shard.md) for the local ModernUO instance, or set the host
and port in `config.local.bat` to play elsewhere.

Two variants of the launcher:

```bat
launchers\game\play.bat --offline       REM no shard; loads data and renders the login screen
launchers\game\play.bat --frames 400    REM run 400 frames and quit
```

Anything else on the command line is passed to the client; see
[Scripted Runs and Probes](Scripted-Runs-and-Probes.md).

## Download a build

The `release` workflow (`.github/workflows/release.yml`) exports the client
with the pinned Godot on GitHub's runners and uploads one artifact per
platform:

| Artifact | What is in it |
|---|---|
| `GUO-windows-x86_64` | `GUO.exe`, `GUO.console.exe`, `GUO.pck` and the .NET data folder |
| `GUO-steamdeck-linux-x86_64` | `GUO.x86_64`, `GUO.pck` and the .NET data folder (SteamOS / Linux x86_64) |
| `GUO-android-arm64-debug` | `GUO-debug.apk`, signed with a throwaway debug key made on the runner |

Where to find them:

- **Actions > release**, pick a run, **Artifacts** at the bottom of its
  summary page (you need to be signed in to GitHub). The workflow runs on
  pushes to `main` that touch the client or the export tools, and on demand
  (**Run workflow**). Artifacts expire after GitHub's retention period.
- **Releases**: a `v*` tag makes a draft release with the Windows and Steam
  Deck builds zipped and attached; the owner publishes it.

**Every build needs your own UO install.** None of them contains any game
data, and none ever will: point the client at your install. The exported
executables read the same environment variables as the launchers
(`UO_CLIENT_DATA`, `UO_SHARD_HOST`, `UO_SHARD_PORT`), or take them on the
command line after `--`:

```bat
GUO.exe -- --play --client-data "<your UO folder>" --host <shard> --port 2593
```

```sh
./GUO.x86_64 -- --play --client-data "$HOME/UO" --host <shard> --port 2593
```

The APK is a debug build with the committed defaults baked in: it reads the
data from `/sdcard/Android/data/org.guo.client/files/uo` and connects to
`127.0.0.1:2593`. Copy your install there and forward the shard port
(`adb reverse tcp:2593 tcp:2593`), or export your own APK with
`UO_SHARD_HOST` set: see [Android Build](Android-Build.md). The Steam Deck
steps, including getting the data onto the Deck, are in [Steam Deck](Steam-Deck.md).

## Optional: the engine on PATH

Add `<your clone>\tools\godot` to `PATH` and `godot` resolves everywhere. Use
`godot` for the interactive editor and **`godot-console` for scripts and CI**:
the console build blocks and writes to stdout; plain `godot` returns at once
and prints nothing.

## Optional: warm the decode cache

```bat
launchers\pipeline\02_warm_cache.bat
```

The client builds its runtime decode cache (`UO_CACHE_DIR`) lazily. Warming
it removes the first-visit hitch. The cache can be deleted at any time.

## Where things land

| | |
|---|---|
| `build\` | Every generated artifact: screenshots, exports, probe output. Gitignored. |
| `%LOCALAPPDATA%\GUO\cache` | The decode cache (`UO_CACHE_DIR`). |
| `godot\GUO\` | The Godot project. Settings, profiles and logs are written under the client's working directory, as in ClassicUO. |
