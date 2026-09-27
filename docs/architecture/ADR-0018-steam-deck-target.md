# ADR-0018: Steam Deck Target

## Status

Accepted — 2026-09-27. `launchers\steamdeck\smoke.bat` exited 0 on a Deck
(SteamOS 3, Desktop mode) holding the UO client data: the files loaded in
2646 ms and the login gump rendered. It needed one fix first: native zlib
is used on Windows only, because upstream's `zlibVersion()` declaration
makes the marshaller free() zlib's static string on Linux (merge of
work/deck-crash, b8f882d).

It was Proposed on 2026-09-26, until a Deck held the data.

What has run on a real Deck so far (SteamOS 3, Desktop mode, over ssh):
`doctor`, `export`, `push`, `run`, `screenshot`, and `smoke` down to its
verdict. The exported client starts on the Deck, brings up Vulkan on the
RADV VANGOGH device, parses its command line and stops at
`[GUO] FATAL: UO client data directory does not exist`, because the data
is not on that Deck yet. The tools do not copy it (section 2), so the
Accepted line above waits for a hand-placed install.

## Date

2026-09-26

## Last Verified

2026-09-26 — on the owner's Steam Deck (LCD, 1280x800, SteamOS 3 with KDE
Plasma 6 on Wayland, glibc 2.41), from a Windows 11 build machine with the
pinned Godot 4.7.2 mono and its export templates. Every claim under
Context that says "measured" was measured that day.

## Decision Makers

Project owner; `uo-port-strategist` (what the port carries to a third
platform); `uo-render-engineer` (display driver, scale).

## Summary

GUO gains the Steam Deck as a platform through Godot's plain Linux x86_64
export of the .NET project: the same project, the same C#, no Deck-specific
code path. What is new is entirely outside the game: a preset template, a
Python tool that exports on the PC and pushes, starts, photographs and
smoke-tests the build on the Deck over ssh, and a launcher script written
onto the Deck. The proprietary UO data is placed on the Deck by its owner,
never by the tools; the Deck's address and key live only in the gitignored
`config.local.bat`.

## Engine Compatibility

| Field | Value |
|-------|-------|
| **Engine** | Godot 4.7.2-stable, mono/.NET |
| **Domain** | Core (export, platform) |
| **Knowledge Risk** | MEDIUM — the 4.7 Linux/BSD export options and the Wayland fallback were read from the 4.7 binary and observed on the device |
| **References Consulted** | `platform/linuxbsd/export/export_plugin.cpp` (4.7), `editor/export/editor_export_platform_pc.cpp`, ADR-0017, the export log `build\steamdeck\export.log` |
| **Post-Cutoff APIs Used** | None in the game; the export plugin's option keys (`binary_format/architecture`, `ssh_remote_deploy/*`, `debug/export_console_wrapper`) as 4.7 declares them |
| **Verification Required** | The login gump on the Deck (the Accepted criterion); a Game-mode launch through Steam; input under gamescope |

## ADR Dependencies

| Field | Value |
|-------|-------|
| **Depends On** | ADR-0017 (the csproj's platform guards, the plugin host compiled out off Windows, the client's own integer scale) |
| **Enables** | A gamepad layer, if one is ever wanted (section 6) |
| **Blocks** | None |
| **Ordering Note** | Shares `godot\GUO\export_presets.cfg` with the Android, Windows and web tools; each renders it before exporting, so only one preset exists at a time |

## Context — measured, not assumed

### What the Deck is

An x86_64 PC: SteamOS 3 is Arch Linux with an immutable system image,
glibc (2.41 on the device), Vulkan through RADV on the Van Gogh APU, a
1280x800 panel (reported to KWin as 800x1280 rotated), 16 GB of RAM. Desktop
mode is KDE Plasma on Wayland with an Xwayland at `:0`; Game mode is
gamescope, Valve's own compositor, with Steam as the launcher. The system
image ships `sshd` (off), `rsync`, `spectacle`, `python3`, `systemd`.

### What a Godot 4.7.2 .NET Linux export is

`--headless --export-debug "Linux" GUO.x86_64` with a preset named `Linux`
produces three things next to each other: the executable (the
`linux_debug.x86_64` template, 70 MB), `GUO.pck` (22 MB, `embed_pck` off)
and `data_GUO_linuxbsd_x86_64/` (79 MB: the self-contained .NET runtime
and the game assemblies, the result of the `dotnet publish -r linux-x64
--self-contained` the exporter runs itself). The publish is a managed
cross-compile, so a Windows build machine needs no Linux toolchain.
`Godot.NET.Sdk` sets `GodotTargetPlatform=linuxbsd` from the runtime
identifier, which is neither `windows` nor empty, so the csproj's
`GuoPluginHost` guard (ADR-0017 section 3) compiles the Windows plugin host
out without a new condition. Measured: the export exits 0 in about a
minute; the only complaints in its log are the editor's own theme-icon
warnings.

The Linux templates come from the same
`Godot_v4.7.2-stable_mono_export_templates.tpz` the Android and Windows
tools unpack into `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`;
the archive holds every platform, and on the owner's machine the Linux
files were already there from the Android setup.

### How the client behaves on the Deck

Measured from `guo.log`:

- Started from an ssh session, Godot's X11 driver fails with
  `Authorization required, but no authorization protocol specified`: the
  session has no `XAUTHORITY` for Plasma's Xwayland. Godot then **falls
  back to its Wayland driver by itself**, opens `wayland-0`, and Vulkan
  comes up (`Vulkan 1.4.330 - Forward+ - Using Device #0: AMD - AMD Custom
  GPU 0405 (RADV VANGOGH)`). With the session's `XAUTHORITY` copied from
  the running `plasmashell`, X11 works too. Either way the client's own
  code runs unchanged and stops, correctly, at the missing data folder.
- **SteamOS sets `KillUserProcesses=True`** for logind
  (`/etc/systemd/logind.conf.d/killuserprocesses.conf`). Everything in an
  ssh session's scope dies when the session closes, `nohup` and `setsid`
  included: measured, a client started that way lived long enough to
  write Godot's banner and nothing more. A process the user manager owns
  survives, so the client is started with `systemd-run --user` as a
  transient unit (`guo-client`), and the user manager's environment
  already carries the desktop session's `DISPLAY`, `WAYLAND_DISPLAY` and
  `XAUTHORITY`.
- The panel sleeps (DPMS off) within minutes of no input. KWin does not
  service a screenshot request for a sleeping output — `spectacle` returns
  0 and writes nothing, and its journal line says the `CaptureScreen` call
  timed out. `kscreen-doctor --dpms on` wakes it, after which the same
  command writes a 1280x800 PNG. The tool wakes the panel before every
  run and every screenshot, as the Android tool wakes the Thor.
- Game mode has no `:0` an ssh session can reach; gamescope owns the
  display and Steam owns the launching. A Steam shortcut to the launcher
  script is the way in, and it is a one-time manual step (section 4).

### Where the client data can live

Anywhere the `deck` user can read: `/home/deck/UO` by default, or the SD
card at `/run/media/deck/<card>/UO`, mounted in both modes. The install is
2 to 4 GB of proprietary files; the tools check for `tiledata.mul` there
and refuse to copy it, for the same reason the Android tool's `push` is a
separate, explicit command and this one does not exist at all: the Deck
is reached over a network, and the project never moves the owner's data
anywhere it was not put by hand.

### What is personal

The Deck's LAN address, the ssh key path and a known-hosts file are the
owner's network. Godot's own `ssh_remote_deploy/*` preset options would put
the host into `export_presets.cfg`, which is gitignored but still a file
that is easy to commit by accident from another tool's rendering; and they
would put the key into the editor settings. Both are off. The values live
in `config.local.bat`, which `.gitignore` names and `tools\guo\config.py`
reads with the same precedence as every launcher.

## Decision

### 1. Godot's Linux x86_64 export, preset from a tracked template

`tools\steamdeck\export_presets.template.cfg` is rendered into the
gitignored `godot\GUO\export_presets.cfg` with one placeholder, the output
path, exactly as the Windows and Android templates are. `embed_pck` is off
so a data-only change is a small push; textures are S3TC/BPTC (a desktop
GPU); no console wrapper (Linux has terminals); `ssh_remote_deploy` off.
The output is `build\steamdeck\{GUO.x86_64, GUO.pck, data_GUO_linuxbsd_x86_64\}`.

### 2. The tool pushes the build and a launcher; never the data

`push` streams the three into `UO_DECK_INSTALL_DIR` (rsync over ssh when
the PC has rsync, a tar stream over ssh otherwise — Git for Windows has no
rsync and the stream took 4 s for 172 MB on a cable) and writes `guo.sh`:

```sh
exec ./GUO.x86_64 -- --play --client-data "$HOME/UO" --host <shard> --port 2593 "$@"
```

The client's command line is the launcher's, not baked into the export as
on Android, because on Linux a script is the natural place for it and it
can be edited on the Deck. The shard is `UO_SHARD_HOST`, with `--host` on
`push`/`smoke` to override; a loopback address is warned about, since on
the Deck it means the Deck.

### 3. The client runs as a user unit; the tool speaks to the desktop session

`run` starts `guo.sh` through `systemd-run --user --unit=guo-client` with
its output appended to `guo.log` beside it; `stop` stops the unit;
`screenshot` wakes the panel and runs `spectacle -b -n -o` (or `grim`) in
the session's environment, copied from `plasmashell`'s; `smoke` chains
export, push, run with `--login-probe-stay --silent`, a wait for
`[GUO] login probe: ok`, a screenshot and a stop, and is the record of
"GUO runs on the Deck". All of it needs Desktop mode.

### 4. Game mode through a Steam shortcut, by hand

`shortcut` writes `~/.local/share/applications/guo.desktop` (GUO in the
Plasma menu) and prints the four steps that add `guo.sh` as a non-Steam
game. Steam's `shortcuts.vdf` is a binary file Steam rewrites on exit;
editing it under a running Steam loses the edit, and the format is
undocumented, so the tool does not.

### 5. Configuration

Six keys in `config.bat`, empty or generic by default, read by
`tools\guo\config.py`: `UO_DECK_HOST`, `UO_DECK_USER` (`deck`),
`UO_DECK_SSH_KEY`, `UO_DECK_KNOWN_HOSTS`, `UO_DECK_INSTALL_DIR` (`~/GUO`),
`UO_DECK_CLIENT_DATA` (`~/UO`). ssh runs with `BatchMode=yes` so a missing
key fails at once instead of prompting inside a launcher.

### 6. Out of scope

A gamepad or trackpad layer in the client (the Deck's *Keyboard and Mouse*
controller template drives the mouse-driven client as it is); a release
export or Flatpak; Game-mode automation; the Android touch layer on Linux.

## Alternatives Rejected

- **Godot's `ssh_remote_deploy`.** Puts the host into the preset and the
  key into the editor settings; runs the game through Godot's own script
  with no place for `guo.sh`, `guo.log` or the login probe. Rejected for
  the personal-data reason above and because the project's tools already
  own the transport for Android.
- **Proton with the Windows build.** Works, presumably, but adds a
  translation layer to a client that has a native Linux build for free,
  and would carry the Windows plugin host into a place it cannot work.
- **`nohup`/`setsid` from the ssh session.** Killed by logind on logout;
  measured (section "How the client behaves").
- **Editing `shortcuts.vdf`.** Binary, undocumented, rewritten by Steam.
- **Pushing the client data.** Refused on principle: the data is the
  owner's and the tool must never be the thing that copies it over a
  network.

## Consequences

- A third platform at the cost of one tool, one preset template and six
  config keys; zero lines in the game.
- The export shares `export_presets.cfg` with three other tools, each
  rendering before it exports. Running two exports at once would race; the
  launchers are sequential by nature.
- The client on the Deck depends on Desktop mode for everything the tool
  does; Game mode is a manual shortcut. Screenshots in Game mode are
  Steam's own (the `...` button), not the tool's.
- The user unit `guo-client` outlives the ssh session on purpose;
  `python tools\steamdeck\run.py stop` is how it ends when the client does
  not end itself.
- `KillUserProcesses=True` and the DPMS behaviour are SteamOS facts a
  future SteamOS may change; the tool's approach is robust to both being
  relaxed.

## Validation

| Step | Ran on 2026-09-26 | Result |
|---|---|---|
| `dotnet build godot\GUO\GUO.csproj` | yes | 0 errors |
| `launchers\dev\smoke.bat` | yes | `[smoke] OK` |
| `launchers\steamdeck\doctor.bat` | yes | every check ok except "UO client data on the Deck" |
| `python tools\steamdeck\run.py export` | yes | `GUO.x86_64` 70 MB, `GUO.pck` 22 MB, `data_GUO_linuxbsd_x86_64/` 79 MB; exit 0 |
| `python tools\steamdeck\run.py push` | yes | 191 files, 172 MB, tar stream over ssh, 4 s; `guo.sh` written |
| `python tools\steamdeck\run.py run --wait 15` | yes | Godot banner, Vulkan on RADV VANGOGH, client args parsed, `FATAL: UO client data directory does not exist: /home/deck/UO`; the unit exits |
| `python tools\steamdeck\run.py screenshot` | yes | 1280x800 PNG of the Plasma desktop, 413 KB |
| `python tools\steamdeck\run.py smoke --no-export --no-push` | yes | reaches its verdict: FAILED (the client exited), log and screenshot saved — the expected result without data |
| `launchers\steamdeck\smoke.bat` with data on the Deck | **no** | the Accepted criterion; needs the owner's install placed at `UO_DECK_CLIENT_DATA` |
| Game mode through the Steam shortcut | no | manual; see docs\steamdeck.md section 6 |
