# Input audit, 2026-10-18

Story INPUT-01. Every input a player uses in each of GUO's three input modes
(ADR-0025), compared with ClassicUO. No code changed for this audit; the fixes
are INPUT-02 (keyboard and mouse), INPUT-03 (gamepad) and INPUT-04 (touch).

## How it was measured

- **Source.** The keyboard and mouse path was read side by side with
  upstream's SDL event filter, `GameController.HandleSdlEvent`
  (`sources/ClassicUO/src/ClassicUO.Client/GameController.cs`, lines 589-927),
  and the files it feeds. Each row cites the upstream file and line.
- **Drift.** Every ported file the input path runs through was diffed
  against upstream after the renamespace. `UIManager`, `Control`, `Gump`,
  `GameSceneInputHandler`, `HotkeysManager`, `MacroManager`, `HotkeyBox`,
  `KeysTranslator`, `StbTextBox`, `DelayedObjectClickManager` and `Keyboard`
  differ only in usings and in marked `PORT DEVIATION (GUO)` hunks for the
  gump presentation scale and the second screen. The double-click window
  (`Mouse.MOUSE_DELAY_DOUBLE_CLICK`, 350 ms), the drag offsets and the
  drag start in `UIManager` are upstream's. So every keyboard and mouse
  difference below sits in the one layer that replaces SDL:
  `godot/GUO/src/Input/GodotInput.cs`, `Input/Mouse.cs`, and the window
  notifications in `Client/GameController.cs`.
- **Gamepad and touch.** Upstream has no gamepad or touch input at all
  (no `SDL_EVENT_GAMEPAD_*`, `SDL_EVENT_FINGER_*` or joystick code anywhere
  under `sources/ClassicUO/src`). For those two modes the CUO column is what
  a ClassicUO player does with the mouse and keyboard to get the same
  result, and the question is whether GUO's pad or finger gets there.
- **Live.** See [Live runs](#live-runs) for what was played and what each
  run showed.

Severity: **1** breaks or misfires a common action with no workaround in
that mode; **2** a real gap with a workaround, or a less common action;
**3** rare, cosmetic, or only for plugin authors.

## Keyboard and mouse

| # | Input | ClassicUO | GUO | Sev |
|---|---|---|---|---|
| K1 | Alt-Tab (or any focus loss) with a modifier held | SDL resets the keyboard on focus loss and raises a key-up for every held key; the filter's `SDL_EVENT_KEY_UP` arm (`GameController.cs:662`) runs `Keyboard.OnKeyUp`, so `Keyboard.Alt/Ctrl/Shift` are false on return | Godot's `Input::release_pressed_events` (engine 4.7.2, `core/input/input.cpp`) clears its own pressed-key tables on focus loss but dispatches no key-up event; `GameController._Notification` (`NotificationApplicationFocusOut`) does not reset `Keyboard`. Alt, Ctrl or Shift stays "held" until that key is pressed and released again, so the next click is a modified click | 1 |
| K2 | AltGr characters (`@ { [ \ ~ €` on German, French, Polish and other layouts) | Windows sends AltGr as LCtrl + RAlt. `Keyboard.OnKeyDown` (`Input/Keyboard.cs:41`) sees `KMOD_RALT \| KMOD_LCTRL`, clears the key and the modifiers, and SDL raises the character as `SDL_EVENT_TEXT_INPUT` (`GameController.cs:679`), which is typed | Godot's Windows display server clears Ctrl and Alt on an AltGr character only while the window's IME is active (engine 4.7.2, `platform/windows/display_server_windows.cpp`, `ke.altgr && ime_active`), and GUO never activates it (K3). `GodotInput.ToKeymod` then reports the Alt as `KMOD_LALT`, so upstream's AltGr test never matches and the key goes on as Ctrl+Alt+key (a Ctrl+Alt macro on that key fires). `GodotInput.HandleTextInput` drops text while Ctrl or Alt is held, so the character is not typed | 1 |
| K3 | CJK input method (Chinese, Japanese, Korean composition) | `TextInputEXT.StartTextInput()` (`GameController.cs:110`) turns SDL text input on, which enables the IME; the composed string arrives as one `SDL_EVENT_TEXT_INPUT` | Nothing turns the IME on (`DisplayServer.WindowSetImeActive` is never called; `GameController.cs` says Godot has no global text-input mode). Composition does not start; only directly typed characters arrive | 2 |
| K4 | Plugin window hook (`OnWndProc`, Razor, ClassicAssist and other assistants) | Every SDL event goes to `Plugin.ProcessWndProc` before the client sees it, and a plugin that returns non-zero swallows it (`GameController.cs:593`) | `Plugin.ProcessWndProc` exists (`Network/Plugin.cs`) but nothing calls it. Plugins still get `ProcessHotkeys` and `ProcessMouse`, which are called in upstream's order | 2 |
| K5 | Keys with no ASCII character: F13-F24, `KP_EQUALS`, layout letters such as `ö ä ü ß é ç`, the ISO `<>` key | SDL's keycode is the Unicode character the key types, or a named code; all are bindable to a macro or hotkey (`Input/KeysTranslator.cs`) | `GodotInput.ToKeycode` maps ASCII and a fixed table; everything else is `SDLK_UNKNOWN`, so those keys do nothing and cannot be bound | 3 |
| K6 | Right-hand modifiers (RCtrl, RShift, RAlt) | Reported as `KMOD_R*`; the masks `KMOD_CTRL/SHIFT/ALT` include both sides | Always reported as the left one. Every test in the ported code uses the both-sides masks, so nothing differs except K2 | 3 |
| K7 | Mouse wheel notch passed to plugins | `Plugin.ProcessMouse(0, (int)wheel.y)` (`GameController.cs:740`): the wheel's own delta | Always `+1` or `-1`. The client's own handling (`OnMouseWheel(isScrolledUp)`) is one step per event in both | 3 |
| K8 | Button press: order of position and press | `Mouse.ButtonPress` then `Mouse.Update` (`GameController.cs:785`); the click position is the last motion's | `Mouse.Update(event position)` then `ButtonPress` (marked deviation, `GodotInput.ButtonDown`). Same position in practice; correct for synthesised input | none |
| K9 | Dragging outside the window | `SDL_CaptureMouse(true)` on a press (`Input/Mouse.cs:44`) keeps motion coming; `Mouse.Update` reads the global position when the pointer is outside (`Mouse.cs:116`) | No capture call, but Godot's Windows display server calls `SetCapture` on every button press (engine 4.7.2, `display_server_windows.cpp`), so motion keeps arriving while a button is held | none |
| K10 | Double click, drag start, held-button walk, wheel zoom, Ctrl+wheel, key repeat, Print Screen, clipboard paste | `Mouse.cs`, `UIManager.cs`, `GameSceneInputHandler.cs` | Same ported code, same constants; Ctrl as the zoom modifier was fixed earlier (the comment in `GodotInput.ToKeymod`) | none |

## Gamepad

Upstream has no gamepad. Rows are the actions a ClassicUO player needs,
taken from `docs/controller_backlog.md` and `docs/wiki/Controller.md`
("What the controller covers"), with what the pad does today.

| # | Action | ClassicUO (mouse/keyboard) | GUO with a pad | Sev |
|---|---|---|---|---|
| P1 | Answer a target cursor on the ground (a field spell, a teleport, a bola) | Left click on a land tile (`TargetManager.Target` from `GameSceneInputHandler`) | The radar picks entities only; there is no ground reticle (backlog T1, todo). The right-stick pointer plus A is a left click at the pointer, so a land tile can be clicked by steering there; whether that is usable is a live check | 1 |
| P2 | Target self / cancel a target | Click yourself; Escape | B cancels (`PadRadar`). No pad input targets yourself: nothing in `Input/Gamepad` targets the player (backlog T1) | 2 |
| P3 | Speech | Type in the chat line and press Enter | Chat strip hidden in gamepad mode; no in-world pad keyboard (backlog S4) | 1 |
| P4 | Bandage self, last object, last spell | `BandageSelf`, `LastObject`, `LastSpell` macros on keys (`MacroManager.cs`) | Not on any default button or the macro row (backlog S1, S2) | 2 |
| P5 | Rebind one action, cancel, reset to defaults | Options > Macros: each macro's hotkey box is set on its own (`Game/UI/Controls/HotkeyBox.cs`), and an unwanted entry is removed by itself | Set controls is a wizard over every row in order; one wrong press means walking the whole set again. No per-row rebind, no reset to defaults (reported by a community player, 2026-10-18) | 1 |
| P9 | Mount a horse | Double-click your mount (`GameActions.DoubleClick` via `GameSceneInputHandler`) | Reported unreliable with the pad (community player, 2026-10-18). Cause: a mount takes a rider within one tile (the dev shard's `BaseMount.OnDoubleClick`), the radar offers mobiles up to 10 tiles away and A sent the double click from where the player stood; the radar never listed the player, so there was no pad double click on yourself to dismount. Fixed in INPUT-00 | 1 |
| P10 | Lock a target on a monster | Click it (last target), or the `TargetNext` macro | Reported unreliable with the pad (community player, 2026-10-18). Cause: in war mode radar A sent a plain double click (upstream attacks, `GameSceneInputHandler.cs:781`) and radar X attacked the previous last target, not the choice. Fixed in INPUT-00 | 1 |
| P6 | Server gumps (quests, vendors, guild, house) | Mouse | Generic focus navigation (`PadGumpNav`, backlog G1) is in, needs a live server-gump pass; vendors, bank and guild have no pad screen (backlog L1, L3) | 2 |
| P7 | Repeat in lists (journal, skills) | Hold the scroll arrow or wheel | D-pad is one step per press on screens; the left stick repeats | 3 |
| P8 | Walk, run, war mode, attack last, target last / next, use, look, context menu, open pack, paperdoll, skills, spellbook, journal, map, options | Mouse and keys | Bound (Controller.md tables) | none |

## Touch

Upstream has no touch input. Rows come from `docs/ui/mobile_playability.md`,
`docs/wiki/Known-Issues.md` and `godot/GUO/src/Input/Touch/TouchInput.cs`.

| # | Action | ClassicUO (mouse/keyboard) | GUO with a finger | Sev |
|---|---|---|---|---|
| T1 | Scroll an Options page or any long list | Mouse wheel over the list | One-finger drag does not scroll; a two-finger pan (`InputEventPanGesture`) is consumed and does nothing (`TouchInput.Handle`). Only the scroll bar's arrows and handle work (Known-Issues) | 1 |
| T2 | Attack last, last target, target next, bandage self | Macros on keys | No key; reachable only through a macro dragged out as a button (mobile_playability "missing") | 1 |
| T3 | War mode by hotkey | Tab | The paperdoll's PEACE/WAR button only | 2 |
| T4 | Drop-down rows in Options | Mouse pick | The classic small rows; easy to mis-tap (Known-Issues) | 2 |
| T5 | Combat macro row chevron | n/a | Defaults to the corner under the holding hand; an Options inset exists but is off by default (Known-Issues) | 2 |
| T6 | Long-press on the world | Right click | Hold on the world is a held right button (walk) by design; a long press only right-clicks over a gump, so a world object's context menu needs a tap (with popups on) | 2 |
| T7 | Pinch over a gump | n/a (gumps do not scale) | Scales that gump (`GumpPresentation.SetScale`); over the world it zooms in steps of 40 px or a 1.12 factor | none |
| T8 | Flick a gump away | n/a | Hold 220-550 ms on a gump then flick (`GumpFlick`) | none |
| T9 | Tap, double tap, drag, hold to walk, pinch zoom, Self/Cancel on target | Click, double click, drag, right-hold, Ctrl+wheel, click/Escape | Verified on the device (Mobile-UI.md) | none |

## Live runs

An agent cannot sit and play for fifteen minutes, so each mode was run
through its scripted session instead, against the dev shard where one is
needed. Each line says what ran and what it showed. Evidence lives in the
shared runs area under `20261018_guo-worker-input_input-audit` and
`20261018_guo-worker-input_input-00`, not in this repo.

| Mode | Run | Result |
|---|---|---|
| Gamepad | `--pad-regression-probe` (synthetic pad, no shard) | 52/52 after INPUT-00 |
| Gamepad | `--pad-wheels-probe` on the dev shard, twice (2026-10-18 22:44Z, 22:55Z) | Wheel, radar, Set controls, screens, mount by walk-up and dismount pass. The shard lacked the pad dummies (patch 0004), so the attack and use dummies were not reached; once the shard sent no target cursor for a party invite |
| Keyboard and mouse | `launchers\dev\playtest.bat` | pending a shard window |
| Touch (desktop layer) | `launchers\dev\touch_probe.bat` | pending a shard window |
| Touch (emulator) | `launchers\android\smoke.bat --avd` | pending the emulator |

K1 and K2 cannot be shown by a scripted run: focus loss and an AltGr layout
come from the operating system, not from an injected event. Both rest on the
engine source cited in their rows.

## What goes where

- INPUT-02 takes K1 and K2 (severity 1), then K3 and K4. K2 and K3 share one fix: turn the window's IME on while a text field has keyboard focus.
- INPUT-00 (ordered first by the director, 2026-10-18) takes P5, P9 and P10.
- INPUT-03 takes P1 and P3.
- INPUT-04 takes T1 and T5 (both on its brief), then T6.
- T2 and P4 overlap: one macro-row default would close both.
