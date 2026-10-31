# Platforms and requirements

This table describes the audited source and existing dated validation, not a
new device certification. The feature matrix for a platform is narrower than
the repository's complete tool inventory. Follow the platform doctor before
exporting; installed SDKs, signing and data paths are separate prerequisites.

| Target | Entry point | Evidence and limits |
|---|---|---|
| Windows 10/11 x64 | [Windows Build](Windows-Build.md) | Main desktop development/export target; native assistant ABI and Windows-only managed .NET Framework host are separate |
| Linux x86_64 | [Linux](Linux.md) | Historical Mint 22/X11 smoke and local-shard guide; ARM64 untested; Windows-only shell twins explain refusal |
| Android ARM64 | [Android Build](Android-Build.md) | Android SDK/JDK/export template and own data needed; debug-signing differs from a release key; device-specific controls/displays need hardware proof |
| Steam Deck / SteamOS | [Steam Deck](Steam-Deck.md) | Linux export pushed by SSH; historical login-screen smoke; Game Mode and all handheld variants are not implied |
| Web browser | [Web Client](Web-Client.md) | Separate community Godot C# web build and WebSocket-to-TCP bridge; historical Chrome local play and Firefox login proof differ |
| macOS / other architectures | [Launcher Reference](Launcher-Reference.md) | A `.sh` file is not evidence of a tested native export; no certification asserted here |

## Build versus run

The desktop C# project targets `net8.0`; Android and web conditionally target
`net9.0`. Current source uses C# language features requiring SDK 10. The pinned
engine is Godot 4.7.2 .NET; non-.NET builds cannot run C#. Python 3.12+ runs the
tooling. Requirements come from [the C# project](../../godot/GUO/GUO.csproj),
[configuration](../../launchers/_shared/config.bat),
[bootstrap](../../launchers/pipeline/00_bootstrap.bat) and the target doctors.
Do not substitute a community web engine for the desktop pin.

An exported game needs its matching `.pck` and assemblies, plus the user's own
UO data. Platform tools never license or supply that data. Web's local data
server and Android/Deck's device copies are distinct deployment arrangements.

## Feature boundaries

- Editor tools (`#if TOOLS`) do not ship in exported clients.
- Labs is an explicit Debug-only build; Release and exports exclude it.
- Client MCP is desktop debug/source only; release, Android and web exclude its listener.
- Mods are desktop opt-in; the current packet ABI does not promise Android/web support.
- The managed assistant host is Windows-only; external assistants have their own licenses/dependencies.
- Browser networking needs the [WebSocket bridge](../../tools/ws_bridge/README.md).
- Controller, touch, foldable and dual-screen tests are different from CPU/GPU performance or firmware tests.

Source boundaries: [project build conditions](../../godot/GUO/GUO.csproj),
[Client MCP](../../tools/guo_mcp/README.md), [Modding](Modding.md),
[Feature Layers](Feature-Layers.md). Check [Feature Coverage](Feature-Coverage.md)
and [Server Compatibility](Server-Compatibility.md) for gaps.

## What a useful compatibility report contains

Record OS/architecture, device model, actual viewport/rendering method,
GUO commit and engine build, UO data version, shard/version, input mode,
enabled effects and the exact action that failed. Keep hardware-driver and
emulator results separate. Redact account, network and personal path details.
