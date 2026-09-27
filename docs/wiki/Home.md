# GodotUO (GUO)

**GodotUO** ports the Classic Ultima Online client to **Godot 4.7 .NET**. It
is a transplant of [ClassicUO](https://github.com/ClassicUO/ClassicUO), not a
rewrite: the network stack, the file readers and the game logic are carried
across as C#, and only the parts that genuinely bind to FNA (the renderer, the
input layer, the audio and the game loop) are reimplemented on Godot.

You supply your own Ultima Online installation. No game data is distributed
with the project, and the client only ever reads your install.

## Status in one screen

| | |
|---|---|
| **Release state** | Pre-release. Playable on a local shard; not yet at pixel parity with ClassicUO. |
| **Port coverage** | 396 / 396 upstream files present, by the audit's count (`docs/port_status.md`). "Ported" means present and compiling, not proven working. |
| **What is verified** | Logs in, walks, opens gumps and hosts assistant plugins against a local ModernUO shard (`launchers\dev\playtest.bat`, `plugin_probe.bat`). Side-by-side sweeps against ClassicUO in eight places: `docs/parity_2026-09-23.md`, `docs/parity_2026-09-26_night.md`. |
| **Platforms** | Windows 10/11 x64 (developed and exported). Android 12+ ARM64 debug APK, run on one device (ADR-0017). Steam Deck (SteamOS, Linux x86_64) export (ADR-0018). Web: plays on a local shard in Chrome (Firefox to the login screen) on a community engine build, see [Web Client](Web-Client.md). |
| **Engine** | Godot 4.7.2 stable, mono/.NET build, pinned and fetched by the bootstrap step. The non-mono build will not work. |
| **Language** | C# on `net8.0` (`net9.0` for the Android export), Python 3.12 tooling. |

## Where to go

- New here: [Getting Started](Getting-Started.md), then [Configuration](Configuration.md).
- Want a build: [Windows Build](Windows-Build.md), [Android Build](Android-Build.md), [Steam Deck](Steam-Deck.md), [Web Client](Web-Client.md), [Dual Screen](Dual-Screen.md), or [Download a build](Getting-Started.md#download-a-build).
- Playing on a phone: [Mobile UI](Mobile-UI.md), [Canvas Background](Canvas-Background.md).
- Planning player helpers: [Player command index and controller research](../player-command-and-controller-research.md).
- Planning the second screen: [OpenMW-DS comparison, tabs, gump transfer, and pinch scaling](../second-screen-ui-research.md).
- Resizing or moving windows: [Gump size and screen controls](Gump-Size-and-Screen.md).
- Need a server: [Dev Shard](Dev-Shard.md).
- Working on the port: [Launchers and Tools](Launchers-and-Tools.md), [Scripted Runs and Probes](Scripted-Runs-and-Probes.md), [Parity and Drift](Parity-and-Drift.md), [Architecture](Architecture.md), [Editor](Editor.md), [Manage Your Shard From the Editor](Manage-Your-Shard-From-The-Editor.md), [Contributing](Contributing.md).
- Short answers: [FAQ](FAQ.md).
- What does not work yet: [Known Issues](Known-Issues.md).

## Licence

GUO is **BSD 2-Clause**, the same licence as ClassicUO. Ported files keep
their upstream copyright header; the licence text and the reviewed upstream
commit are in `docs/upstream/`. The ModernUO patches in
`tools/modernuo/patches/` are GPL-3.0 because they modify ModernUO, which is
not redistributed. The `.claude/` agents and skills are MIT, adapted from
Claude Code Game Studios. Godot is MIT and is fetched, not redistributed.

Ultima Online is a registered trademark of Electronic Arts Inc. GUO is an
unofficial, fan-made project with no affiliation to Electronic Arts or
Broadsword Online Games, and it contains none of their game data.

## How this wiki is kept honest

Every "works" statement on these pages traces to one of three things: an
ADR's Validation section (`docs/architecture/`), a tool README's run log
(`tools/*/README.md`, the "what has actually been run" tables), or a commit
message that names the evidence. Where the sources themselves say a thing is
written but not exercised, the page says **written, not verified**.
