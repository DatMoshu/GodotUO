# ADR-0025: The gamepad on by default, and the input mode follows the last input

## Status

Accepted — 2026-09-28 (the owner's decision).

## Date

2026-09-28

## Last Verified

2026-09-28, against work/integration f8e1280 plus work/pad-default-guo2.

## Decision Makers

The owner, relayed by the director (GUO-Director, 2026-09-28). Written by
GUO2, who owns the input side; GUO-UI owns the glyphs.

## Summary

A gamepad works in GUO on every platform, and by default on the Windows
desktop too, where upstream ClassicUO has no pad at all. Options >
"Use a controller" (`Profile.Gamepad`, on by default) turns it off, and
then a pad does nothing.

The client tracks which input the player is using (keyboard and mouse,
gamepad, or touch) and switches the moment another one is used, with no
delay, the way console-style PC games hot-swap. The pointer and the UI's
button glyphs follow the mode.

## Engine Compatibility

Godot 4.7.2 mono. It uses Godot's own joypad events (`InputEventJoypadButton`,
`InputEventJoypadMotion`), `Input.GetJoyName`, and the device id
`InputEvent.DeviceIdEmulation` that marks mouse events Godot makes from a
touch. There is no new engine feature, no GDExtension, and no SDL outside
Godot.

## ADR Dependencies

- **ADR-0006** (GameController as a Godot node): `_Input` is where every event
  arrives, so the input mode is noted there first.
- **ADR-0017** (Android) and **ADR-0018** (Steam Deck): the platforms where the
  pad was already on. This extends it to the Windows desktop.
- **ADR-0024** (Modern gumps): GUO-UI's glyphs in Godot views follow
  `InputMode.Changed`.

## Context

Review P1 (2026-09-28) found that GamepadInput acted on the Windows desktop,
where upstream has no pad handling, and the fix (d685dac) gated the pad off
there by default for desktop 1:1 parity, behind an opt-in. The owner has
overridden that. A pad is how GUO is played on the handhelds (Thor, Odin,
Steam Deck), and the same player should be able to pick one up on a PC
without looking for a setting.

## Decision

1. **On by default everywhere.** `GamepadInput.Enabled` follows
   `Profile.Gamepad` (default true) on every platform. The opt-in
   `Profile.GamepadOnDesktop` is gone; a profile that saved it simply loses
   the key.
2. **Input mode (`GUO.Input.InputMode`).** `Current` is `KeyboardMouse`,
   `Gamepad` or `Touch`. Every input event passes through
   `InputMode.Note` in `GameController._Input` before anything else:
   - a pad button, or a stick past the 0.5 deadzone → Gamepad;
   - a key, a mouse button, or the mouse moving 4 px or more → KeyboardMouse;
   - a touch → Touch. Godot's mouse events made from a touch (device -1)
     are not a mouse.
   The pointer that the pad's right stick moves goes straight to
   GodotInput, not through `_Input`, so it never switches the mode back.
   `Changed` fires on every switch, and also when another pad or another
   `PadFamily` is picked up while the mode stays Gamepad.
3. **Glyphs (GUO-UI).** `InputMode.PadFamily` (Xbox, PlayStation, Nintendo,
   SteamDeck, Generic, from the pad's name) picks the glyph sheet;
   `InputMode.ButtonFor(PadAction)` gives the printed button that does a
   job, or null when the pad's layout is unknown (its face buttons do nothing
   yet) and for the D-pad, sticks and triggers.
4. **The pointer.** In Gamepad mode the client's cursor is hidden while the
   pad's pointer is idle (4 s after the right stick or A last acted). It shows
   again the moment the right stick moves or the mouse is used, and it stays
   while a target cursor is up, an item is held or a window menu is open,
   since then it says where A lands.

## Consequences

- The Windows desktop is no longer 1:1 with ClassicUO when a pad is
  connected. Without a pad it is unchanged: the mode starts as KeyboardMouse,
  and nothing hides the cursor.
- The Options "Controller buttons" section shows on the desktop too, since
  the off switch has to be reachable there.

## Validation Criteria

GamepadProbe on the desktop (`--gamepad-probe`) checks:
- a pad works by default;
- a pad button switches to Gamepad, once;
- a key switches back at once;
- 12 px of mouse motion switches back, and 1 px does not;
- the pointer hides when idle, returns on the right stick, and is kept for
  a target cursor;
- with "Use a controller" off, a pad does nothing.

## GDD Requirements Addressed

The owner's decision (2026-09-28, relayed by the director): the gamepad is
on by default on every platform, desktop included, with AAA-style hot
swapping. The last input used (keyboard and mouse, or pad) sets the mode,
the pointer and UI glyphs follow it, and switching back is instant.

## Related

- ADR-0017 (Android), ADR-0018 (Steam Deck).
- docs/thor-controller-layout.md: how the Thor's layouts were measured.
- `godot/GUO/src/Input/InputMode.cs` and `godot/GUO/src/Input/Gamepad/GamepadInput.cs`.
