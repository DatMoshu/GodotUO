# GodotUO documentation

GodotUO (GUO) brings Classic Ultima Online to Godot .NET. It carries over
ClassicUO's C# game logic, networking and data readers, with Godot hosting
the rendering, input, audio and editor tools. You provide your own legally
obtained UO installation; the repository contains no client data.

**Pre-release.** Source coverage is not a promise of complete gameplay or
pixel parity. This documentation audit targets `main` at `fc9e3588`
(2026-10-28). Historical test dates and experimental features are identified
separately. See [Feature Coverage](Feature-Coverage.md) for evidence and gaps.

## Choose your starting point

| I want to… | Start here |
|---|---|
| Install, build or update GUO | [Getting Started](Getting-Started.md), [Install and Update](Install-and-Update.md) |
| Connect and play | [Servers and Accounts](Servers-and-Accounts.md), [Gameplay and Settings](Gameplay-and-Settings.md) |
| Change rendering or recover a broken view | [Renderer and Performance](Renderer-and-Performance.md), [Troubleshooting](Troubleshooting.md) |
| Use a controller, touch or a second screen | [Controller](Controller.md), [Mobile UI](Mobile-UI.md), [Dual Screen](Dual-Screen.md) |
| Browse and edit UO assets | [Assets and Fiddler Workflows](Assets-and-Fiddler.md), [Editor](Editor.md) |
| Build worlds, multis or server content | [World Editing](World-Editing.md), [Building Multis](Building-Multis.md), [Author UO Data](Author-UO-Data.md) |
| Run a server or check compatibility | [Dev Shard](Dev-Shard.md), [Server Compatibility](Server-Compatibility.md) |
| Automate GUO or use editor AI | [AI and MCP](AI-and-MCP.md), [Scripted Runs and Probes](Scripted-Runs-and-Probes.md) |
| Extend or contribute | [Modding](Modding.md), [Architecture](Architecture.md), [Contributing](Contributing.md) |

## What ships where

| Surface | Boundary |
|---|---|
| Desktop client | Faithful port plus player settings; production Depth World on supported rendering methods, canvas recovery path |
| Optional presentation | HD art, regional audio, realtime/spell lighting, styled water and ambient effects require their settings and/or installed content |
| Godot editor | Assets, World, Gumps, MapGen, Multis, Store, Admin and AI tools; editor code is excluded from exported clients |
| Labs | Explicit `GuoLabs=true` Debug build; experiments are excluded from all exports |
| Platforms | [Platform Support](Platform-Support.md): [Platform Support](Platform-Support.md): [Windows](Windows-Build.md), [Linux](Linux.md), [Android](Android-Build.md), [Steam Deck](Steam-Deck.md), [Web](Web-Client.md); evidence differs by target |

For exact boundaries see [Feature Layers](Feature-Layers.md). For every
documented tool and launcher see the [Tool Reference](Tool-Reference.md)
and [Launcher Reference](Launcher-Reference.md). These inventories describe
available source entry points, not proof that every workflow has been run.

## Visual guides and evidence

[Visual Guide](Visual-Guide.md) records real captures and their limits.
Architecture, asset and automation diagrams explain the paths below the UI.
Screenshots illustrate GUO; they do not distribute the source game data.

## Project and licensing

[What is GUO?](What-Is-GUO.md) · [Known Issues](Known-Issues.md) ·
[FAQ](FAQ.md) · [Roadmap](Roadmap.md) · [Community and Support](Community-and-Support.md)

GUO and ClassicUO use BSD 2-Clause; Godot is MIT. See the repository
[license](../../LICENSE) and [upstream notices](../upstream/).
Ultima Online is a trademark of Electronic Arts. This is an unofficial fan
project, unaffiliated with Electronic Arts or Broadsword Online Games.
