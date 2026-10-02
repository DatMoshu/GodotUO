# Controller

GUO plays with a gamepad on every platform, including the Windows desktop,
where upstream ClassicUO has no gamepad support at all. The controller is on
by default. To turn it off, untick Options > **Use a controller**; a pad then
does nothing.

## Hot swap

GUO follows whichever input you used last, the moment you use it, the way
console-style PC games do. There is no setting to flip.

| You use | GUO switches to | What changes |
|---|---|---|
| A pad button, or a stick pushed past halfway | Gamepad | The mouse cursor hides; the right stick moves the pointer; on-screen hints show your pad's buttons. |
| A key, a mouse button, or the mouse moved 4 px or more | Keyboard and mouse | The normal cursor and keyboard hints. |
| A finger on the screen | Touch | The touch layer ([Mobile UI](Mobile-UI.md)). |

## Default layout

Buttons are named by what's **printed** on your pad. GUO works out the layout
from the pad's name, so an Xbox A and a PlayStation Cross both confirm.

| Control | Action |
|---|---|
| D-pad or left stick | Walk |
| Right stick | Move the pointer |
| A (Cross) | Left click at the pointer: confirm, pick, use |
| B (Circle) | Escape: cancel a target cursor, leave a text field |
| Y (Triangle) | Open or close the touch bar's macro row |
| X (Square) | The window menu (size, lock, which screen) for the topmost window, on mobile layouts |
| Back / Select / View | Open or close the one-screen drawer |

While the window menu is open, the D-pad moves between its controls instead
of walking, A presses the selected control, and B closes the menu.

## Button glyphs

On-screen hints use glyphs for your pad's family (Xbox, PlayStation, Nintendo,
Steam Deck or generic), from the CC0 Kenney Input Prompts set. If GUO can't
tell the layout, it shows no glyph, and the face buttons wait until you pick
the layout.

## Handhelds

- **AYN Thor** (dual screen): the built-in pad works in both Standard and Xbox
  mode. In Xbox mode GUO undoes the pad's A/B and X/Y swap.
- **Steam Deck:** see [Steam Deck](Steam-Deck.md).
- **Android:** see [Android Build](Android-Build.md) and [Dual Screen](Dual-Screen.md).

## For developers

- The design record is ADR-0025 (`docs/architecture/ADR-0025-gamepad-on-by-default.md`).
- The code is `src/Input/Gamepad/GamepadInput.cs` and `src/Input/InputMode.cs`.
- `--gamepad-probe` checks the bindings with scripted pad events (see
  [Scripted Runs and Probes](Scripted-Runs-and-Probes.md)).
