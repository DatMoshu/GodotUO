# What is GUO?

**GodotUO (GUO) is a game client for Classic Ultima Online**, the program players run on their own
machine to connect to a shard. It is not a server or an emulator: it doesn't replace RunUO, ServUO or
ModernUO. It connects to them, just as the official client and ClassicUO do.

GUO is a port of **ClassicUO** (CUO) to the **Godot 4** engine. ClassicUO's C# (its network code, file
readers and game logic) is carried across as it is; only the parts that were tied to its old graphics
framework (FNA) are rebuilt on Godot: drawing, input and sound. The first goal is to behave exactly
like ClassicUO. New features come on top of that, and never at the cost of it.

You bring your own Ultima Online installation. GUO contains no game data and never writes to your
install.

## Is it a client or an emulator?

A client. Here's the split:

| | What it is | Examples |
|---|---|---|
| **Server / emulator** | Runs the world: accounts, NPCs, items, combat rules | RunUO, ServUO, ModernUO, Sphere |
| **Client** | What the player runs to see and play that world | the official client, ClassicUO, **GUO** |

GUO speaks the same network protocol as ClassicUO, so it works with the same shards. Day to day it's
developed and tested against a local ModernUO shard; the editor also talks to ServUO.

## How is it different from ClassicUO?

| | ClassicUO | GUO |
|---|---|---|
| Engine | FNA (a reimplementation of XNA) | Godot 4 (.NET / C#) |
| Game code | the original | ClassicUO's, ported with every change marked and checked in CI |
| Windows, Linux, macOS desktop | yes | Windows now; Linux as the Steam Deck build |
| Android phones and handhelds | no | yes, with a full touch layer (see below) |
| Dual-screen handhelds (AYN Thor) | no | yes: the world on top, your windows on the bottom |
| Steam Deck | via Proton/Linux | native Linux build with its own deploy tools |
| In a web browser | no | yes, plays on a shard in Chrome (a community engine build; early) |
| Assistant plugins (Razor etc.) | yes | yes on Windows, through ClassicUO's plugin interface |
| Built-in world and art editor | no | yes, inside the Godot editor (see below) |
| Content packs / Asset Store | no | yes: backgrounds, screensavers, themes, sounds, layout presets |
| Licence | BSD 2-Clause | BSD 2-Clause (keeps ClassicUO's copyright) |

On a desktop with a mouse, GUO aims to look and play exactly like ClassicUO. The mobile features below
only switch on for touch screens.

## What GUO adds

- **Touch play:** a touch bar, a combat macro row, Self/Cancel while targeting, finger-sized grid
  containers, pinch to resize any window, and hold-and-flick to move, close or reset a window.
- **Two screens:** on dual-screen handhelds your paperdoll, backpack, journal and status live on the lower
  screen, and you can send any window to either screen.
- **The GodotUO editor:** browse all the UO art, gumps, animations, hues and maps; view the real world;
  paint land, place statics and whole houses; import your own art and gumps as PNG; place spawners and
  decorations, and push them to a running shard live, with no restart. Edits are kept in a project on
  top of the map, never in your install, and export to ModernUO or ServUO.
- **The Asset Store:** a catalogue you can run yourself. The client installs, updates and removes packs,
  checking every file. It never accepts UO game data.
- **Quality of life:** a canvas background around the game, OLED screensavers, a boot splash, per-platform
  default settings, and downloadable builds for every platform from each change.

## What it isn't (yet)

- **Pre-release.** It logs in, walks, trades, opens gumps and runs assistant plugins, but it's not yet pixel-identical to
  ClassicUO everywhere. Known Issues lists the gaps.
- **Not a server.** To play you need a shard (your own, or one you trust).
- **No game data.** Every build needs your own UO installation.

## Where to go next

- [Getting Started](Getting-Started.md): install, point GUO at your UO folder, play.
- [Known Issues](Known-Issues.md): what's rough today.
- [Web Client](Web-Client.md), [Android Build](Android-Build.md), [Steam Deck](Steam-Deck.md),
  [Editor](Editor.md): one page per platform or tool.
- [FAQ](FAQ.md): short answers.

*Ultima Online is a registered trademark of Electronic Arts Inc. GUO is an unofficial fan project, not
affiliated with or endorsed by Electronic Arts or Broadsword Online Games.*
