# Mobile UI

What makes ClassicUO's mouse-and-keyboard interface playable with fingers,
and how the client decides which defaults a platform gets. Sources:
`docs\ui\mobile_playability.md` (the verified / written / missing table),
`docs\ui\grid_container.md`, `src\Configuration\PlatformDefaults.cs`, and the
commit messages of the `work/ui` chain, each of which names what was seen on
the device.

## Per-platform defaults

One table in `Configuration\PlatformDefaults.cs` holds every profile default
that differs by platform. Desktop has one set (version 5: a full-size world and wheel zoom); everything
else on the desktop is exactly upstream's. Mobile gets a full-size borderless world, pinch zoom kept, large
and scaled containers, grid containers, fitted container placement and the
low-power background. Web gets a full-size world and wheel zoom. The platform
comes from `OS.HasFeature` (`mobile`, `web`); `UO_PROFILE_PLATFORM` overrides
it on the desktop for checks.

### Profile versions and migration

`Profile.ProfileVersion` records which table a profile has seen. Each entry
in the table carries a `since` version. When a profile is loaded that is
below the current version (a new one, or one saved before an entry existed),
every field that **still holds upstream's default** takes the platform's
value; a field the player changed is kept. The player never has their own
choice overwritten.

| Version | Added |
|---|---|
| 1 | The table itself: window, zoom and container sizes for Mobile and Web. |
| 2 | `FitContainerPlacement` on Mobile. |
| 3 | `GridContainers` on Mobile. |
| 4 | The canvas background entry, taken by the background branch (see ADR-0016). |
| 5 | Desktop: the world fills the window and the wheel zooms, as on Mobile and Web, instead of upstream's 600x480 world in the corner of a large window. Unticking "Game window full size" under Options gives the smaller, movable world back. |
| 6 | `CanvasBackgroundLowPower` on Mobile and Web (ADR-0016). |

Migration from 2 to 3 was verified on the device (commit "GridContainerGump:
containers as a grid"), and to 6 by the canvas background work.

## The touch layer

`src\Input\Touch\TouchInput.cs` translates Godot's screen touch, drag and
magnify events into the mouse events `GodotInput` already consumes, so the
ported game code sees a mouse. Off the touch layer, the desktop path is
unchanged; every hook into ported code is marked `PORT DEVIATION (GUO)`.

| Gesture | Becomes |
|---|---|
| Tap | Left click |
| Second tap | The game's own double click |
| Hold on the world | Held right button: walk, then run |
| Hold on an item | Held left button: pick up, then drag |
| Long press | Right click |
| Pinch | Ctrl + wheel steps (zoom); accumulated from magnify gestures |
| Tap on the gump bar | The top-bar action |

Verified on the device: login by touch alone, hold-to-walk, pinch in and
out, drag, split and drop into the bank, double-tap use (commits "Android:
fullscreen, soft keyboard..." and "GridContainerGump..."). On the desktop,
`launchers\dev\touch_probe.bat` drives synthetic fingers through the layer
and checks 18 gestures against the shard; `--touch` enables the layer on the
desktop and `--touch-trace` logs each gesture decision.

### The touch bar

`TouchGumpBar.cs` is a CanvasLayer overlay along the bottom, drawn with the
top bar's own button art and clilocs: six finger-sized actions. While a
target cursor is up it shows **Self** and **Cancel** so a spell or a bandage
can be aimed and a target can be abandoned without a keyboard (commit
"Touch bar: Self and Cancel while a target cursor is up").

### The soft keyboard

A tap on a text field raises the soft keyboard and slides the field's root
gump up by what the keyboard covers; the world gump is never moved. The
account field asks for the e-mail keyboard and the password field for the
password keyboard, the two types an IME must not autocorrect in.

## Grid inventory

`GridContainerGump` shows a container as a grid of finger-sized slots instead
of upstream's free-placed items. It is a new gump, not a `ContainerGump`
subclass: `OpenContainer` keeps upstream's classic path and then calls
`GridContainerGump.ReplaceClassic`, the one upstream hunk. The grid polls its
container's contents, saves as `GumpType.Container` so `gumps.xml` restores
it through the classic path, pages when it would exceed the screen height,
and has a filter field, a sort toggle and a Classic button. Slots call the
same `GameActions` as `ItemGump`.

Options, in the Containers section: "Open containers as a grid of slots"
(`GridContainers`), a slot size slider from 32 to 80 (`GridContainerSlotSize`,
default 44) and the fitted placement checkbox. Changing the slot size rebuilds
open grids. The grid takes as many columns (three to six) as fit half the
client width so two containers still sit side by side.

## Container placement on a phone

Upstream cascades containers from the top left, which puts the backpack on
the character. With `FitContainerPlacement` on, `ContainerPlacement` keeps a
remembered spot unless it covers the character, and otherwise picks the
right-most, top-most free spot inside the client area, below the top bar and
above the touch bar, covering the least of the character and the other open
gumps. Gumps restored from `gumps.xml` at login are moved off the character
the same way. Off on the desktop, and off whenever "override container
location" is set.

## The screen scale

On a phone the touch layer sets the client's whole-number `ScreenScale` to
fit the display, dividing the OS scale out, without touching the global
setting. Options writes the screen zoom back only when its slider moved, so
pressing Apply for something else does not shrink the UI (commit "Options:
apply the screen zoom only when its slider moved").

## What is still missing

`docs\ui\mobile_playability.md` keeps the table of every interaction a
player needs, marked verified, written or missing. Read it before claiming a
phone is playable; some rows have moved since it was written (target
cancelling is now on the touch bar, but the table still says missing).
