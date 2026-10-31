# Troubleshooting

Start with the doctor launcher for your platform. Each one prints what's
missing and how to fix it:

```bat
launchers\dev\smoke.bat          desktop: engine, project import, C# build, client data, editor add-on
launchers\android\doctor.bat     Android: SDK, JDK, templates, keystore, device
launchers\windows\doctor.bat     Windows export: templates and the icon check
launchers\steamdeck\doctor.bat   Steam Deck: this machine and the Deck, over ssh
launchers\web\doctor.bat         web: the engine fork and its .NET SDK
```

## Quick answers

- **A headless build stops after three minutes.** `launchers\dev\build.bat`
  stops a build that runs longer than `UO_BUILD_TIMEOUT` (180 seconds by
  default) and exits 124. Raise it in `config.local.bat`.
- **The emulator smoke will not start.** It needs about 10 GB of free memory.
- **An NPC says nothing.** NPC speech is silent until a voice pack covers
  that NPC, and the Sound option is on ([Content Packs](Content-Packs.md)).
- **Enhanced HD or a screen effect does nothing.** The pack is not
  installed, or the option is off; both are off by default.

## Setting up

**The bootstrap fails, or `tools\godot` is empty.**
Rerun `launchers\pipeline\00_bootstrap.bat`. It downloads the pinned Godot 4.7.2
mono build and clones the ClassicUO reference. Behind a proxy, download the
engine by hand into `tools\godot` (see `tools/godot/README.md`).

**"C# not supported" or the project opens without scripts.**
That's the non-mono Godot. GUO needs the **mono/.NET** build, and the launchers
use the pinned one. `GODOT_EXE` overrides it only if you know you need to.

**`godot` prints nothing from a script.**
Use `godot-console`. The plain executable returns immediately and writes
nothing to stdout.

**`dotnet build` can't find `cuoapi.dll`.**
The client compiles against upstream's `cuoapi.dll` in `sources\ClassicUO`.
Run the bootstrap. A git worktree finds the main checkout's `sources\` by
itself (`tools/msbuild/UpstreamDir.props`); if that checkout keeps it
elsewhere, set `UO_UPSTREAM_DIR` (see [Contributing](Contributing.md)).

## Client data

**"No UO client data" or missing files.**
Set `UO_CLIENT_DATA` in `launchers\_shared\config.local.bat` to the folder
holding your `.uop` and `.mul` files, then run
`launchers\pipeline\01_verify_client_data.bat`. It checks the install against
the known file list and reports the version it found.

**Wrong art, or a crash on load.**
Set `UO_CLIENT_VERSION` if your client isn't the default version. A
`needs_custom_data` shard needs that shard's own client files: see
[Servers and Accounts](Servers-and-Accounts.md).

**Slow first start.**
The first run decodes art into the cache. `launchers\pipeline\02_warm_cache.bat`
builds it ahead of time (see [Getting Started](Getting-Started.md)).

## Connecting

**The client connects to nothing.**
Nothing listens on `127.0.0.1:2593` until a shard runs there. Start the
[Dev Shard](Dev-Shard.md), or set `UO_SHARD_HOST` and `UO_SHARD_PORT`, or pick a
server on the Servers tab.

**Login works, then the shard drops you.**
Check whether the shard needs encryption or a specific client version, and
check that its emulator allows third-party clients. Logging in with the same
account from a second client disconnects the first.

**Android can't reach a shard on your PC.**
The Android download connects to `127.0.0.1:2593`, which on the phone is the
phone itself. Forward the port over USB with `adb reverse tcp:2593 tcp:2593`,
or export your own build with your shard's address
([Android Build](Android-Build.md)).

## Platforms

**An Android update won't install over the old one.**
Builds from before the stable signing key used a fresh key each time. Uninstall
the old app once; later builds install over each other.

**The Steam Deck build won't start.**
Run `launchers\steamdeck\doctor.bat`. It checks ssh, the Deck's files and the
.NET runtime on the Deck. See [Steam Deck](Steam-Deck.md).

**The web build.**
Godot can't export C# to the web yet, so the web client needs a community
engine build. See [Web Client](Web-Client.md) and ADR-0008.

## Still stuck

- Search [Known Issues](Known-Issues.md) and the [FAQ](FAQ.md).
- Ask in GitHub Discussions (Q&A), or open an issue with the bug template. Say
  your platform, client version and shard emulator, and attach the client log.
  Never attach game data or screenshots of proprietary art you can't share.
- Security problems go through `SECURITY.md`, never a public issue.

## Current renderer and upgrade recovery

For rendering defects, first try [canvas recovery](Renderer-and-Performance.md#recover-with-canvas)
with the same shard/profile/location. Record the actual rendering method and
viewport. A still image cannot establish frame pacing or remote animation.
For mixed exports, keep the executable, `.pck` and .NET data folder from one
build together; use [Install and Update](Install-and-Update.md) to preserve
the previous install/profile. Diagnose a scratch profile before deleting a real one.

If editor automation is unavailable, check the [AI preference and MCP policy](AI-and-MCP.md).
An unreadable permission file denies calls; keep a copy before repairing it.
Missing provider credentials, a stopped local service and a denied permission
are separate causes. Turning on AI does not grant server mutation access.
