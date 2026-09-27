# ADR-0009: Second Display

## Status

Accepted — verified on an AYN Thor on 2026-09-26: the paperdoll, backpack,
status bar and journal on the lower screen, the world alone on the upper one,
a tap on the lower screen reaching the client, and no measurable cost to the
main screen's frame rate (see Validation and `tools/android/README.md`).

## Date

2026-09-26

## Last Verified

2026-09-26 — AYN Thor (Android 13, main 1080x1920, second 1080x1240, both
369 dpi, adb serial `<thor-serial>`), package `org.guo.dual`, against the dev shard
over the LAN; and the desktop simulator (`--dual-screen 1240x1080`) against
the dev shard on 127.0.0.1.

## Decision Makers

Project owner; `uo-render-engineer` (render targets, input);
`uo-port-strategist` (a platform feature with no upstream counterpart).

## Summary

On a device with a second display that Android will let an app present on,
GUO uses it as a shelf for the four gumps a player keeps open all session —
paperdoll, backpack, status bar, journal — and the main display shows the
world alone. The second display is modelled as a **virtual extension of the
client window to the right**: a shelf gump has an X of `MainWidth + x` and is
otherwise an ordinary gump that upstream's `UIManager` lays out, hit-tests,
sorts, saves and draws exactly as it would on a wide desktop window. Around
that, three small additions: the UI render lists are drawn a second time
into a target the size of the second screen with a `-MainWidth` translation;
that target's pixels are pushed to an `android.app.Presentation` through the
engine's own Java bridge (no plugin, no Gradle, no NDK); and touches on the
presentation come back as fingers at `mainWidth + x`, so every gesture the
touch layer knows works unchanged. With no second display, nothing is added
to the tree and nothing changes. A profile setting, offered only where a
second display exists, turns it off.

## Engine Compatibility

| | |
|---|---|
| Godot | 4.7.2 stable mono; `JavaClassWrapper` and the `AndroidRuntime` singleton as shipped in the 4.7 Android template |
| .NET | net8.0, Mono JIT on android-arm64 (ADR-0017) |
| Android | API 24+ in principle; `Presentation` is API 17; verified on 13 |
| Desktop | inert, except the opt-in simulator (`--dual-screen WxH`) |

## ADR Dependencies

- ADR-0001 (render/presenter seam), ADR-0002 (batcher on canvas items):
  the second screen is one more `RenderTarget2D` fed by the same batcher.
- ADR-0006 (GameController as a node): the draw hook sits in `DrawFrame`.
- ADR-0017 (Android target): the touch layer, the scale model, the tooling.

## Context

### Problem Statement

The AYN Thor has two screens. A UO client on the upper one alone spends a
third of a 1920x1080 window on gumps that cover the world, and on a phone
every gump also covers the fingers' walking room. The lower screen is idle.

### Current State

Before this ADR the client drew one window and knew of no other display.
`TouchInput` (ADR-0017) turns fingers into mouse events for that window.

### What Godot 4.7 on Android can do with a second display — measured

Read from the 4.7 sources (`core/extension/java_class_wrapper.cpp`,
`platform/android/java/lib/.../AndroidRuntimePlugin.kt`) and the template's
dex, then confirmed on the device:

- **No plugin is needed.** Every exported Android template carries the
  `AndroidRuntime` engine singleton (`getActivity`, `getApplicationContext`,
  `createRunnableFromGodotCallable`) and `JavaClassWrapper` (`Wrap`,
  `CreateSamCallback`, `CreateProxy`, `GetException`). Together they reach
  any Android framework class from C#: constructors are called by the class's
  own name, nested classes with `$`, overloads by argument count and type;
  `byte[]` marshals as `PackedByteArray`, Java `Object[]` as a Godot `Array`
  of `JavaObject`, a `Callable` as a Godot-side callable a Java SAM interface
  can wrap. This is what a Gradle plugin would have written in Kotlin, with
  none of the build.
- `DisplayManager.getDisplays(DISPLAY_CATEGORY_PRESENTATION)` is the
  capability check: it returns the displays an app may open a `Presentation`
  on. On the Thor it returns display 4, "Screen-2", physical 1080x1240,
  rotation 1 (so 1240x1080 as presented), `FLAG_PRESENTATION`, touch
  `EXTERNAL`. On a phone it returns nothing.
- `android.app.Presentation` is a `Dialog` bound to a display. It must be
  built and shown on Android's UI thread (its constructor makes a `Handler`),
  which `activity.runOnUiThread(createRunnableFromGodotCallable(callable))`
  does; a Godot `Callable` invoked from that thread into C# worked on the
  device without ceremony (the concern was Mono thread attachment; it was
  unfounded).
- A frame crosses as a `Bitmap` refill: Godot's `Image` in `Rgba8` is byte
  for byte Android's `ARGB_8888` memory order, so
  `Bitmap.copyPixelsFromBuffer(ByteBuffer.wrap(bytes))` + `View.postInvalidate()`
  moves a frame with two copies and no per-pixel work. The `ImageView`
  scales the bitmap up to the panel with `FIT_XY`, and its drawable's bitmap
  filter is switched off, so the integer scale stays nearest-neighbour
  (rule 7).
- Touch on the presentation arrives as `MotionEvent`s on the UI thread, read
  through a `View.OnTouchListener` SAM callback into a queue the Godot thread
  drains once a frame.
- The SurfaceFlinger id for `screencap -d` is the display's `uniqueId`
  (`local:4630946482288158084` for the Thor's lower screen), which
  `dumpsys display` prints and `dumpsys SurfaceFlinger --display-id` lists;
  the tooling reads it from there.

### Constraints

- Parity: the moved gumps must behave exactly like upstream's. Only where
  they are drawn may differ.
- Rule 7: nothing on the way to the second panel may filter pixel art.
- The main screen's frame rate must not drop.
- No second display means no change at all, on Android or the desktop.
- Every edit to ported code is marked `PORT DEVIATION (GUO)`.

### Requirements

Detection and size/dpi exposed to C#; a `GUO.Platform.Android` class that is
a no-op elsewhere; the feature with a profile switch; tooling that reports
the display, includes the feature in an export and photographs both panels;
this record.

## Decision

### Architecture

Two files under `src/Platform/Android/`:

- **`SecondDisplay`** — the Java side. `Find()` runs the capability check and
  reads id, name, physical size (rotated) and rotation; `Open(w, h)` builds the
  presentation with an `ImageView` over a `w x h` `ARGB_8888` bitmap on the UI
  thread; `Present(rgba)` refills the bitmap; touches queue into
  `TryDequeueTouch`. Every Java call is wrapped: a vendor Android missing a
  method sets `LastError` and disables the feature instead of throwing in
  the frame. `IsSupported` is `OperatingSystem.IsAndroid()` plus the singleton
  being present, so the class is inert everywhere else.
- **`DualScreen`** — the feature, a `Node` added to the tree only when a
  display exists (or the desktop simulator is asked for). Once the player is
  in the world and the profile allows it, it becomes **active**: it makes a
  `RenderTarget2D` of the second screen's logical size (physical divided by
  the client's `DpiScale`: 620x540 on the Thor at 2x), opens the
  presentation with a bitmap of that size, and resizes the world viewport to
  the whole main window the way upstream's "game window full size" does on a
  resize (the profile flag itself is left alone).

  **The shelf.** Each frame, a shelf gump seen for the first time —
  `PaperDollGump` of the player, `ContainerGump` of the player's backpack,
  any `StatusGumpBase`, `JournalGump`/`ResizableJournal` — that lies on the
  main screen is moved to its corner of the shelf (`X += MainWidth`). A gump
  that already sits on the shelf, from a saved position, is left there; a
  gump whose saved position falls outside the shelf is placed again. After
  that first placement the gump is the player's: dragging, closing, saving
  and restoring are upstream's, with the one clamp change below. Nothing is
  placed before it has a size.

  **Drawing.** `GameController.DrawFrame` calls `DualScreen.Draw` right
  after `UIManager.Draw`, while the render lists still hold the frame's
  gumps. It points the batcher at the second target, begins with a
  `-MainWidth` translation, clips off the world viewport's five-pixel
  border (which upstream hangs past the window edge, off screen on a
  desktop and down the left of the shelf here), and calls
  `UIManager.RedrawLists`, a marked addition that draws the lists again
  without clearing, rebuilding or re-sorting anything. Every fourth frame
  the target is read back and pushed (about 15 Hz for gumps, which change
  slowly; the main screen keeps its rate).

  **Touch.** A finger on the presentation at physical `(x, y)` becomes a
  synthetic `InputEventScreenTouch`/`Drag` at window pixels
  `(mainPhysicalWidth + x', y')` with the panel's scale undone, so after the
  input layer divides by `DpiScale` the client's `Mouse.Position` is
  `(MainWidth + x/scale, y/scale)`: exactly where the gump is. Pointer ids
  are offset by 32 so they never collide with the main screen's fingers. The
  touch layer's tap, hold, long-press and drag then apply unchanged.

  **Off.** When the profile flag is cleared, or the player leaves the world,
  the presentation is dismissed, the target freed, and every gump is put
  through upstream's own `SetInScreen`, which brings the shelf back onto the
  main screen. Once per session with the feature off, the same call rescues
  gumps a previous dual-screen session saved beyond the window.

### Key Interfaces

```csharp
namespace GUO.Platform.Android
{
    internal sealed partial class DualScreen : Node
    {
        public static bool HasSecondaryDisplay { get; }   // a display, or the simulator
        public static int SecondWidth { get; }             // physical, as rotated
        public static int SecondHeight { get; }
        public static bool Active { get; }                 // display + profile + in game
        public static int MainWidth { get; }               // logical; where the shelf begins
        public static int ExtraWidth { get; }              // Active ? shelf width : 0
        public static void Setup(Node host, string simulate, bool off);
        public static void Draw(UltimaBatcher2D batcher, RenderTarget2D restore);
    }

    internal sealed class SecondDisplay
    {
        public static bool IsSupported { get; }
        public static SecondDisplay Find();                // null when there is none
        public void Open(int bitmapWidth, int bitmapHeight);
        public bool Present(byte[] rgba);
        public bool TryDequeueTouch(out TouchEvent e);
    }
}
```

Ported code touched, each site marked `PORT DEVIATION (GUO)`:
`Gump.OnDragEnd` and `Gump.SetInScreen` add `DualScreen.ExtraWidth` to the
window width they clamp against (zero when inactive); `UIManager.RedrawLists`;
the one call in `GameController.DrawFrame`; `Profile.DualScreenEnabled`
(default true); an `OptionsGump` checkbox under "Gumps & Context", built only
when `HasSecondaryDisplay`.

Bootstrap: `--dual-probe` (log in, open the four gumps, wait for the shelf,
measure, report `[GUO] dual screen: ...` lines, photograph both screens,
stay up on a device), `--dual-screen WxH` (desktop simulator: a real OS
window with a nearest-filtered `TextureRect`, its mouse forwarded as one
finger), `--dual-off`.

Tooling: `run.py doctor` prints the device's displays and the presentation
one; `run.py displays`; `run.py dual_probe` exports with `--dual-probe`,
runs, waits for the verdict and photographs both panels with
`screencap -d <id>`; `launchers\android\dual_probe.bat`;
`UO_ANDROID_SECOND_DISPLAY` (an id override for screencap; blank reads it
from `dumpsys display`).

### Implementation Guidelines

- Never special-case a gump's *behaviour* for the shelf. If a gump needs to
  know it is on the second screen, the model is wrong.
- Anything that reaches the panel goes through `SecondDisplay`; the client
  never sees a Java object.
- Keep the readback rate a constant in `DualScreen` (`PresentEvery`); it is
  the only knob that trades second-screen latency for main-screen time.

## Alternatives Considered

### Alternative 1: A Godot Android plugin (Kotlin, Gradle) with a second `SurfaceView`

Rendering a second Godot viewport straight into a Java surface would avoid
the readback. Rejected: it needs the Gradle build, the NDK and the Android
source template (ADR-0017 rejected those for the same reasons), and Godot
4.7 has no API to render a viewport into a foreign surface anyway; the
plugin would still end up copying pixels.

### Alternative 2: A second Godot `Window`

Godot's multi-window support is desktop only; on Android a `Window` is
embedded in the main viewport. Kept as the desktop simulator, where it is
exactly right.

### Alternative 3: Present the whole UI target and move the world instead

Rejected: the UI target is the main window's size and full of the world's
overlays; the shelf model keeps the main screen's compositing untouched and
the second target minimal.

### Alternative 4: A `Presentation` that shows a status/HUD of GUO's own design

A dedicated second-screen layout (a big status bar, a chat panel) would fit
the panel better than upstream gumps do. Rejected for now under rule 3 —
parity first; the shelf shows upstream's gumps unchanged, and a purpose-built
layout is an improvement to propose separately once the mechanism is proven.

### Alternative 5: Thor-specific detection

Rejected: `DISPLAY_CATEGORY_PRESENTATION` is the general Android fact; no
model check was needed.

## Consequences

### Positive

- The world fills the upper screen; the four gumps are always in reach on
  the lower one, at the same 2x pixel scale, unfiltered.
- No plugin, no Gradle, no NDK: the export is the same one ADR-0017 makes.
- Desktop unchanged: no display, no node, `ExtraWidth` is zero, the two
  clamp deviations add nothing.
- Cost measured: none on the main screen (60/60 on the device).

### Negative

- At 2x the shelf is 620x540 logical, and the modern status gump (577x216)
  plus the journal (345x298) cannot both fit beside the paperdoll and the
  backpack without overlap; the journal, opened last, lands on top of the
  status bar's right half. The player can drag them; positions are saved by
  upstream. A small-screen layout is an improvement for a later ADR.
- A gump that starts on the main screen and is wider than it (the top bar,
  1114 wide on a 1011-wide window) shows its overflow on the shelf, as a
  window spanning two desktop monitors would. The top bar is also
  redundant next to the touch gump bar; "Disable Menu" hides it.
- No cursor is drawn on the second screen; with touch there is none to draw.
- Readback is a GPU sync every fourth frame (4-5 ms on the Thor, hidden
  under vsync). A bigger panel or a higher rate would show.
- Seven ported files carry marked deviations; each is two to ten lines.

### Neutral

- `Profile.DualScreenEnabled` is saved in the profile; a desktop profile
  shared with the device carries it, harmlessly, since it is read only where
  a display exists.
- The window Godot reports on the Thor after the profile is applied is
  2022x1078, not the panel's 1920x1080 (the client applies its saved window
  bounds and Android does not refuse them). Everything here is derived from
  the same `ClientBounds`, so shelf, drawing and touch agree with each other;
  the number itself belongs to ADR-0017's window model.

## Risks

- Another vendor's Android may report a presentation display the panel does
  not actually show, or refuse `Presentation.show`; then `LastError` is set,
  `Ready` never becomes true, and the shelf still works as a wide window
  the player cannot see. The probe would report `presented 0`.
- Rotation: the Thor's second display is rotated 90 degrees by the OS; the
  size is read after rotation and the bitmap fills it. A device that rotates
  at run time is not handled (the presentation would need re-opening).

## Performance Implications

Measured on the Thor by `--dual-probe`, 180 frames each, same scene: main
screen **60.0 fps with the second screen running, 60.0 with it suspended**;
53 frames pushed during the on-measurement (every fourth frame); last
readback-and-push 4.0-4.7 ms. Desktop simulator on a 3840x2054 main window
with a 1240x1080 second: 47-57 fps on vs 57-60 off, the readback there
being four times the pixels of the Thor's 620x540 target and the machine
already at the edge with a 4K main window.

## Migration Plan

None needed: a profile without `DualScreenEnabled` reads it as true; gumps
saved beyond the window by a dual-screen session are rescued by
`SetInScreen` on a single-screen run.

## Validation Criteria

1. `dotnet build godot\GUO\GUO.csproj` — 0 errors. **Met.**
2. Desktop, `--play --dual-probe --dual-screen 1240x1080`: shelf 4 gumps,
   world fills the main window, both frames saved. **Met** (`build\android\
   desktop_dual_main_2026-09-26.png`, `desktop_dual_second_2026-09-26.png`).
3. Thor, `run.py dual_probe --args "--host <shard-lan-ip>"`: `[GUO] dual
   screen: ok`, presentation shown, frames pushed, fps on = off, both panels
   photographed by `screencap`. **Met** (`thor_dual_main_2026-09-26.png`,
   `thor_dual_second_2026-09-26.png`).
4. A touch on the second panel reaches the client: `adb shell input -d 4
   tap 430 164` on the paperdoll's OPTIONS button logged `first touch ...
   (client 1227,82)` and opened the options gump on the main screen.
   **Met** (`thor_dual_main_after_tap_2026-09-26.png`).
5. The options checkbox turns the feature off and the shelf comes back.
   **Written, not exercised on a screen** — the code path is
   `Deactivate` → `SetInScreen`, the same call upstream makes.

## GDD Requirements Addressed

None registered; `tr-registry.yaml` is empty for this project.

## Related

- `tools/android/README.md`, "Second display" and the run table.
- `src/Platform/Android/SecondDisplay.cs`, `DualScreen.cs`;
  `src/Bootstrap/DualProbe.cs`.
- ADR-0017 for the touch layer, scale and export this builds on.
