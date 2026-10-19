# Known issues

GodotUO is pre-release. These are the problems we know about in the current
build, written for players. If you hit something that is not on this list,
please report it (see [Contributing](Contributing.md)).

Desktop login and in-game resizing were checked on October 19, 2026.
Phone and combat items were reviewed in code on October 18 and still need
fresh runtime checks on a device or the emulator.

## On phones and handhelds (Android)

- **The lower screen is crowded.** On a dual-screen handheld such as the AYN
  Thor, the paperdoll, status bar, journal and backpack overlap on the lower
  screen at the default size. You can move them by dragging. A smaller
  second-screen scale (Options, "Fine second screen scale": 1.0x, 1.25x or
  1.5x) gives them more room, but it is not the default yet, and at 1.25x and
  1.5x some pixels come out slightly wider than others.
- **The chevron sits in the bottom-right corner.** The small tab that shows
  and hides the combat macro row sits where a hand holding the device may
  rest. Options has "Touch bar chevron inset from
  the corner" to move it in; the default is still the corner.
- **Back can ask to quit while windows are open.** On a dual-screen
  handheld, the Back button closes windows on the top screen but leaves the
  ones on the lower screen alone, because those are meant to stay put. If the
  only open windows are on the lower screen, Back asks "Quit Ultima Online?".
  Choose Cancel, or press Back again, to carry on playing.
- **Scrolling lists in Options.** Dragging a finger over an Options page does
  not scroll it. Use the scroll bar at its right edge: tap its arrows or drag
  its handle.
- **Drop-down lists have small rows.** The lists in Options (for example the
  background picker) use the classic client's small rows, so pick carefully.
- **The canvas background rarely shows.** On a phone the game world fills the
  whole screen, so a background chosen in Options, including one from the
  Store, is only visible where the world does not reach.
- **No music unless you copy it.** If your copy of the client data on the
  device has no `Music` folder, the game plays no music.
- **Your backpack grid order lasts one session.** Items keep their slots while
  you play, but the order starts afresh the next time you log in.

## Combat

- **Next Target does not pick the nearest creature.** It steps through the
  creatures the client knows about in its own order, exactly as ClassicUO
  does, so it may choose a cat across the road before the rat at your feet.
  Tap the creature directly if you want a particular one.
- **Attack Last does not walk you to your target.** It starts the attack; you
  still have to get close, as in ClassicUO.

## On a desktop

- **The login window is small.** The classic desktop login starts at
  640x480. Enlarging the window leaves the login panel at the same size in
  the top-left corner, with the canvas background around it. Rechecked on
  October 19 at 640x480 and maximized to 3840x2054, with the optional 3D
  pre-game screen disabled.

The October 19 desktop in-game check reached the world and resized between
640x480 and 3840x2054. The world followed the window size; the paperdoll,
backpack and journal opened at their classic sizes and overlapped at their
default positions. This check does not verify the lower-screen layout,
phone gestures, backpack grid persistence or combat targeting.

## Everywhere

- **Not yet pixel-identical to ClassicUO.** The world and gumps are close,
  but side-by-side comparisons still find differences.
- **The web client is local-only and runs on a community engine.** It plays
  on a local shard in Chrome and reaches the login screen in Firefox, but
  the page, your install and the WebSocket bridge all run on your PC, the
  exporting engine is an unsigned community build of Godot 4.7.2, the first
  start takes about a minute, and the server list shows no latency. See
  [Web Client](Web-Client.md).
