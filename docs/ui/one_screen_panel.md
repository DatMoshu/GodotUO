# The second screen on one screen: the side panel and the drawer

Owner's direction, 2026-09-28. A device with one screen (a phone, a tablet, the unfolded Fold, the desktop) gets the
Thor's lower screen as well, inside its one window. Code: `src/Platform/Android/DualScreen.Panel.cs`. It follows
[uo_godot_style.md](uo_godot_style.md): only the client's own art, whole pixels, and colour for state only.

## How it works

The Thor's lower screen is a virtual extension of the client window to the right: a gump on it has
`X >= MainWidth` and is otherwise an ordinary gump. `DualScreen.Draw` renders those gumps a second time, into a
target the size of that screen (ADR-0009). The one-screen panel keeps all of that and changes only the two ends:

- **Out.** The target is not read back and pushed to a display. Its own texture is shown by a `TextureRect` on a
  `CanvasLayer` (layer 80) over the main window, at the client's whole-number scale, sampled nearest.
- **In.** A finger or the mouse on that rect is moved onto the extension (`mainWidth + x`) in front of the client
  (`DualScreen.HandleMainInput`, called from `GameController._Input`). It is then delivered the way the lower
  screen's fingers are. A finger that came down on the panel stays the panel's until it lifts. Once it leaves the
  rect, it is where it is on the main screen, so an item carried out of the drawer drops under the finger.

The shelf, the pre-game card and the companion tabs do not know the difference.

## Three shapes

| Shape | When | Where | Holds |
|---|---|---|---|
| Dock | Login screen, and the window has room for the 640-wide login gump and a dock of 280–400 client px | Left edge, full height; the login gumps are centred in the rest | The pre-game card (Servers, Settings) |
| Drawer | In the world | Over the left or right edge (Options), at most 620 client px wide, the Thor's lower screen at 2×. 48 px of world is always left beside it | The shelf gumps and the companion tabs |
| Split | In the world, on a near-square window (longer side ≤ 1.34 × shorter), if chosen in Options | The bottom half; the world gets the top half | The same |

Without room for the dock (a phone in portrait, the Thor's lower screen used alone at 1240×1080), the login screen
keeps the card's modal behind its Servers button.

On a landscape window where one step less of the touch layer's screen scale makes room for the dock, the scale takes
that step, but never below 2. The unfolded Fold at 2076×1557 is 3× with 52 px spare, and 2× with a 382 px dock.

## The drawer

- **The tab.** A stone tab (the card frame, 0x13BE) 28×60 art px, at `UoTheme.PixelScale`, on the drawer's inner
  edge. That is the window's edge while the drawer is closed. The classic gumps' gold page arrow (gump 0x15E1,
  16×16) points the way a tap will move it, flipped rather than a second texture (GUOUI's review: the client's own
  art over a drawn one). A hand-drawn ink chevron stands in only if the client data has no such gump.
- **Open or close.** A tap on the tab does either. A drag on the tab pulls the drawer with the finger, and letting go
  past a third of the way decides. A tap on the world beside an open drawer closes it and does nothing else, unless an
  item is held: then it is a drop. Slides take 0.14 s.
- **The pad.** Back (Select, the Xbox View button), free until now, opens and closes it. While a pad is the input in
  use, its glyph (`pad_back`, Kenney tile 616) sits under the tab, and Options > Controller buttons lists it.
- **Captions.** The window menu and the flick chips say "Side panel" (the split: "Bottom panel") and "Main screen"
  instead of the Thor's "Bottom screen" and "Top screen".

## Settings

`Profile.OneScreenPanel` is 0 to follow the platform, 1 for on and 2 for off. On the touch layer it is on by default;
on the desktop it is opt-in. There, the login window is widened by the dock (`LoginScene.Load`).
`OneScreenDrawerSide` is 0 for left and 1 for right. `OneScreenSquareLayout` is 0 for the drawer and 1 for the split.
All three are in Options on one screen, beside the shelf's own checkboxes ("Use the side panel as a shelf for
gumps"). Before a profile exists they come through `DualScreenSettings` like the rest. `--one-screen on|off` overrides
the setting for a run.

## Probes

`--one-screen-probe` checks the login screen (the dock where there is room, else the Servers button). It then logs
in and checks the drawer: closed at first with its tab, the shelf gumps on the extension, and a tap on the tab
opening it. It also checks a tap inside landing on the extension, a tap outside closing it, the pad's Back and its
glyph, a drag on the tab both ways, the right edge, and the split on a near-square window. Each state is
photographed. `--pregame-probe` treats the dock as the card's second screen.

Measured on 2026-09-28:

| Where | Size | Result |
|---|---|---|
| Desktop, `--one-screen on --window-size 1920,1080` | 1920×1080 at 1× (login window 976×480) | 11/11; pregame probe 21/21 with the dock |
| Emulator, the Thor-bottom AVD | 1240×1080 at 2× (620×540) | 10/10 (no dock: modal; split offered) |
| Emulator, `wm size 1920x1080` | 1920×1080 at 2× (960×540) | 9/9, dock 304 px |
| Emulator, `wm size 2076x1557` | 2076×1557 at 2× (1038×778) | 11/11, dock 382 px, split |

## Not done yet

- The world viewport keeps upstream's 640×480 minimum (`WorldViewportGump.ResizeGameWindow`). In the split on a
  window under 960 client px tall, the world is taller than its half, and its bottom is under the panel.
- The shelf packing (`DualScreen.Place`) is the Thor's. In a drawer narrower than 620 px or a split under 540 px
  tall, gumps overlap more than they do on the Thor.
- The pre-game card's own Settings do not offer the one-screen options yet. Options does.
