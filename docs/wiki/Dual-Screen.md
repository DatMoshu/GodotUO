# Dual Screen

On an Android device with two displays, GUO can put the world on one screen
and a shelf of gumps (paperdoll, backpack, status bar, journal) on the other.
Source of truth: ADR-0009 (Accepted, verified on one two-screen handheld on
2026-09-26) and `tools\android\README.md`. It is optional: a device with one
display is unaffected, and the probe reports "one display" and passes.

## How it works

- `src\Platform\Android\DualScreen.cs` and `SecondDisplay.cs` ask
  `DisplayManager` for a display in Android's presentation category and open
  an `android.app.Presentation` on it through Godot's own Java bridge
  (`JavaClassWrapper`); no plugin, no Gradle build. A phone with no such
  display gets null from the lookup and nothing else in the file runs.
- The frame crosses as one bitmap copy per frame, and the view's bitmap
  filter is off, so the integer scale up to the display stays
  nearest-neighbour (rule 7). Every Java call is wrapped: a missing method
  on another vendor's Android disables the feature instead of throwing.
- Shelf gumps are placed at `MainWidth + x`: the client thinks its window is
  one wide canvas, and the second display shows the right-hand part. Nothing
  in the ported gump code knows there are two screens.
- A tap on the lower screen reaches the client as a mouse event at the
  shifted position.
- The profile key `DualScreenEnabled` turns it on for a character; an
  Options checkbox exists for it, **written, not exercised on a screen**
  (ADR-0009 says so).

## Flags and settings

Supported floating gumps now have a UI handle for size/reset/lock and transfer
between screens, plus pinch-to-scale in touch mode. See
[Gump size and screen controls](Gump-Size-and-Screen.md) for the supported
types and current validation limits.

| | |
|---|---|
| `--dual-probe` | Logs in, moves the shelf gumps to the second screen, prints `[GUO] dual screen: ok` when it is up. `launchers\android\dual_probe.bat` bakes it into an export, runs it and photographs both displays into `build\android\dual_main.png` and `dual_second.png`. |
| `--dual-screen WxH` | Desktop simulator: a second window of that size stands in for the second display, so the layout can be checked without a device. Verified against the dev shard on loopback (ADR-0009 validation 2). |
| `--dual-off` | Ignore the second display even if the profile asks for it. |
| `UO_ANDROID_SECOND_DISPLAY` | Force a display id; empty lets the client take the presentation-category display it finds. `python tools\android\run.py displays` lists them. |

## What was verified

ADR-0009's validation section: `dotnet build` clean; the desktop simulator
with the shelf holding four gumps and the world filling the main window (two
frames saved under `build\android\`); on the device, the paperdoll, backpack,
status bar and journal on the lower screen, the world alone on the upper one,
a tap on the lower screen reaching the client, and no measurable cost to the
main screen's frame rate.

A separate background agent is extending this to open the second screen from
launch with a welcome page and settings; that work is not on this branch and
is not described here.

## Generalising beyond one device

Everything the client asks the OS for goes through `DisplayManager` and
`Presentation`, which any Android 12+ device with a second display exposes
(a foldable's outer display, an external monitor over USB-C, a two-screen
handheld). Only the one handheld has been tried; the vendor-tolerance path (a missing
Java method disabling the feature) is **written, not verified**.
