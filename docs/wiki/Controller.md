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
from the pad's name, so an Xbox A and a PlayStation Cross both confirm. Every
job except walking and the pointer can be moved to another input with
**Set controls** (below).

| Control | Action |
|---|---|
| D-pad or left stick | Walk |
| Right stick | Move the pointer |
| A (Cross) | Use: left click at the pointer (confirm, pick, use) |
| B (Circle) | Cancel: Escape (a target cursor, a text field, a menu) |
| X (Square) | Attack your last target. On mobile layouts (touch screens), the window menu (size, lock, which screen) for the topmost window instead |
| Y (Triangle) | Open or close the macro row when that row is on the touch bar; otherwise the Macros screen |
| LB | Target last (sends an open target cursor to your last target) |
| RB | Next hostile (selects the next hostile as your target) |
| LT, held | The menu wheel |
| RT, held | The interact radar |
| L3 (left stick click) | Always run, on or off |
| R3 (right stick click) | War mode, on or off. **LB + R3** cycles the debug UI scale (see Scale); it does not walk |
| Start / Menu / Options | The options |
| Back / Select / View | Open or close the one-screen drawer |

While the window menu is open, the D-pad moves between its controls instead
of walking, A presses the selected control, and B closes it.

## The menu wheel (LT)

Hold LT and eight windows ring your character, drawn in the client's own
art, while the game keeps running. Each slice is the window's **icon only**.
Push either stick toward one (the middle is "none"): it lights gold, steps
out, and its **name** shows in the centre. The other slices stay unnamed.
Let go of LT to open it. The ring stays the size it has at the automatic
UI scale; LB+R3 does not grow or shrink it.

Each window is a full-screen controller screen: the D-pad moves, A acts,
B closes, LB and RB change page. No mouse. The pack, the paperdoll, skills
and the spellbook still ask the shard for what it owns (the pack's contents,
the paperdoll, skill values, the book), but the screen is up at once from
what the client already has. Chat, the quest log, the guild and the mini map
are not on the wheel: those calls only asked the shard, and nothing opened
when it did not answer. The status bar is the same when it is already on
screen, so Status is a screen of the character's numbers instead.

| Slice | Opens |
|---|---|
| Top | Backpack |
| Top right | Paperdoll |
| Right | Journal |
| Bottom right | Skills |
| Bottom | Spellbook |
| Bottom left | World map |
| Left | Macros |
| Top left | Options |

- Let go with the stick in the middle: nothing opens.
- A quick **tap** of LT (under 0.2 s, no stick) opens the last window the
  wheel opened again.
- B while the wheel is up closes it without opening anything. B on a screen
  closes the screen.
- Which window sits in which slice is chosen at the end of Set controls.
  Status and Party are offered too.

## The interact radar (RT)

Hold RT and everything usable within 10 tiles is marked on the ground:
people, monsters and animals, doors, corpses, containers and movable items.
Hostiles (criminals, murderers, enemies) are marked red, everything else gold.
Each mark is an **icon** (the thing's art, when the client has a static for
it) on its tile. Names are not drawn on every mark.

- **Right stick:** the choice jumps to the nearest thing the way you push.
- **LB / RB:** step to the previous / next thing, nearest first.
- The highlighted choice gets the client's own highlight, the target brackets
  of the "new target system", a **name plate** (only that one), and a card at
  the foot of the screen saying what each button does. The card and the name
  plate shrink to stay on screen at the large debug UI scales:

| Button (default) | With a choice |
|---|---|
| A | Use (a double click; in war mode on a mobile, an attack, as the client does) |
| X | Look (a single click: its name) |
| Y | Its context menu |
| B | Close the radar |
| Let go of RT | Use, if you chose something and pressed nothing; otherwise nothing |

**Target cursors.** When a spell, skill or item puts up a target cursor, the
radar is how a pad picks the target: hold RT, choose, and A sends the target
to the choice; B cancels the cursor (and leaves the radar up).

The reach is `radarRange` in `padbindings.json` (1 to 24 tiles, 10 by default).

## Set controls

Options > Video > Controller buttons > **Set controls...**, and offered once
after your first login with a pad (B on that card skips it; it is not offered
again).

1. Every job is a row: the action, the glyph for what it is set to, and
   that input's name. Nothing else. The order is Use, Cancel, Attack last,
   Target last, Toggle war mode, Next hostile, Always run, Macro row, Menu
   wheel, Interact radar, Options, Drawer. The row being set is highlighted.
2. **Press and hold** the button, trigger or stick direction you want for
   the highlighted row. The meter fills while exactly one input is held
   (0.7 s); then it is taken and the next row lights. Let go before the
   next one counts.
3. Then the wheel's eight slices: choose each one's window with the D-pad and A.
4. Everything is applied together at the end. Giving one input to a job takes
   it away from any other job.

**Cancel:** hold three or more inputs at once, at any point. Nothing changes.

Triggers and stick directions count as inputs (on past 60%, off under 30%).
A stick direction bound to a job no longer walks or moves the pointer that way.

The choices are saved in `padbindings.json` beside `settings.json`, so they
belong to this install, not to one character.

## Button glyphs

On-screen hints use glyphs for your pad's family (Xbox, PlayStation, Nintendo,
Steam Deck or generic), from the CC0 Kenney Input Prompts set. If GUO can't
tell the layout, it shows no glyph, and the face buttons wait until you pick
the layout.

## Handhelds

- **AYN Thor** (dual screen): the built-in pad works in both Standard and Xbox
  mode. In Xbox mode GUO undoes the pad's A/B and X/Y swap.
- **Steam Deck:** see [Steam Deck](Steam-Deck.md). The right trackpad
  works as the mouse at all times, alongside the pad: Steam Input sends it
  as mouse motion and its click as a left click. On the Deck (and in any run
  Steam launched, or Game Mode) with a pad connected, moving or clicking the
  mouse moves and shows the one shared pointer but does not switch the hints
  away from the pad; only a real key does. The right stick and the trackpad
  move the same pointer, whichever moved last wins. While LT or RT is held,
  the trackpad does not change the wheel's slice or the radar's choice.
- **Android:** see [Android Build](Android-Build.md) and [Dual Screen](Dual-Screen.md).

## Full-screen screens and the half-cut camera

When a menu-wheel window is open it is a controller screen on the **right
half** of the client. The world camera is centred on the **left half** so the
player sprite stays visible (Diablo-style), but the world is **not clipped**
there: it keeps rendering out to the right edge of the window, under the
screen. System chat stays in the left half. Closing the screen restores the
normal camera.

The **backpack** screen is the client's open-pack gump (`0x003C`, 230×204)
with the **side straps cropped off**, then as a **9-slice** that **fills that
right half** as a square-ish satchel (bag body + open flap, no handles). The
slice margins follow the classic container pocket (**left 44, top 65, right
186, bottom 159** in the original art), rewritten for the cropped texture.
Corners keep that shape and scale with the bag; edges stretch on one axis;
the center is the pocket. The bag is **transparent over the live world**:
the gump's transparent texels stay transparent, so beside and around the bag
art you see the map, not a filled panel, not the grey canvas and not leather
pixels spread into the gaps. Nothing is painted behind the bag. Items sit in
a grid **inside** that pocket, on clear cells so the bag shows through (the
focused cell is an outline). Icons use each item's **DisplayedGraphic** (so
a gold pile shows the pile static for its amount, not a single coin), cropped
to opaque pixels and drawn at **native size** (or shrunk to fit — never
upscaled past 1× with nearest filtering). A scrollbar appears when there are
more items than the visible cells. The focused item's name and details sit
**under the grid**, in the bag's lower band, in cream ink so they read on the
leather. Items stay in container order. Button hints (**A** use, **B** close,
**X** drop, **Y** equip, **Back** context menu) sit on the **left half**,
**right-aligned against the bag's left edge**, in a plate only as wide as
those words — not in the bottom-right corner and not on the far left of the
world half.

The **paperdoll** screen does the same with the client's paperdoll base
(`0x07d0`): the doll art is the panel background, worn slots are a grid
inside it, details below, hints right-aligned to the panel. **A** use, **X** unequip into
the pack, **Y** use again, **Back** context menu, **B** close. Classic pack
and paperdoll gumps stay hidden on the left while the pad screen is open.

### Scale

List screens (journal, skills, …) can change **menu scale** inside their
stone frame without moving the frame: the frame stays glued to the right half
of the client; only the content scales from the top-left inside it. Backpack
and paperdoll ignore menu scale — their gump art fills the panel. **Menu
font** is per panel family (backpack, paperdoll, skills, …). **Journal font**
(chat text) is a separate scale and does not follow the menu font. Options on
the Options screen cycles each for a quick check: Menu scale, Menu font,
Journal font. **Menu scale still cycles 1, 2, 3, and 1 is the smallest.**
It is not the debug UI scale below.

**Debug UI scale** is the pad overlay's art-to-window scale
(`PadOverlay.UiScale`). Until you touch it, that is the automatic whole
number from the screen density, and it is never below 1. **LB + R3** (hold
LB, click the right stick) moves to the next **smaller** step. The steps are
**0.5, 0.75, 1, 2, 3 and 4**. After 0.5 the next press wraps to 4 (the
largest). From an automatic 2 the presses go 1, then 0.75, then 0.5, then 4,
then 3, then 2. **0.5 and 0.75 are below the old minimum of 1.** Lone R3
still toggles war mode. The D-pad is not used for this and still walks (or
moves the grid focus while a screen is open). This chord is not in Set
controls.

The bag does **not** shrink or grow with that scale. At a 1280×800 client
the 9-slice **is** the right half, **640×800 window pixels, at every step**.
LB+R3 only changes how many grid squares fit in the pocket. Cells stay
between 26 and 44 art pixels, so a larger scale means fewer, bigger squares
inside the same bag. It does not resize the bag panel, the menu wheel, or
the frame. `PadScreen.PreviewBackpack` is that grid, with the scrollbar reserved:

| UI scale | Columns × rows | Cells |
|---|---|---|
| 0.5 | 15 × 20 | 300 |
| 0.75 | 10 × 13 | 130 |
| 1 | 8 × 10 | 80 |
| 2 | 4 × 5 | 20 |
| 3 | 2 × 3 | 6 |
| 4 | 2 × 2 | 4 |

The detail line sits under the grid in the bag's lower band (cream on the
leather). Hints sit on the left half, right-aligned to the bag's left edge.
The menu wheel stays the size it has at the automatic scale. At 0.5 and 0.75,
hint and detail text is drawn larger in art pixels so it stays about as tall
on screen; the Options menu-font value itself does not change.


## Gamepad mode hides the PC UI

While the input mode is **Gamepad**, or while a pad screen is open, classic
mouse gumps (the pack window, paperdoll gump, top bar, and the rest of the
ClassicUO UI stack) are not drawn. The touch command bar is hidden too. The
world viewport still draws. The pad's own screens (wheel, radar, PadScreen,
Set controls) are Godot layers and stay. Switching back to keyboard/mouse or
touch shows the PC UI again (unless a pad screen is still open); nothing was
disposed.

## What the controller covers

Honest list of player-facing UO systems. "Bound" means a pad path exists
today (wheel screen, radar, or a default button). "Not yet" means a player
still needs mouse/touch or it is unfinished. Second-player systems are
marked separately: the client can open them, but a probe cannot pass them
against one shard character.

| System | Controller | Notes |
|---|---|---|
| Walk / run | Bound | D-pad / left stick; L3 always-run |
| Use / double-click | Bound | A at pointer; radar A; backpack/paperdoll A |
| Cancel / close | Bound | B |
| Attack last | Bound | X (default); radar A in war mode |
| War mode | Bound | R3 |
| Target last / next hostile | Bound | LB / RB |
| Interact (look, use, context) | Bound | RT radar: X look, A use, Y context |
| Backpack | Bound | LT wheel → backpack screen (grid, drop/equip/context) |
| Paperdoll / equipment | Bound | LT wheel → paperdoll screen |
| Journal | Bound | LT wheel → journal screen |
| Skills (use skill) | Bound | LT wheel → skills screen (A uses a clickable skill) |
| Spells / spellbook | Bound | LT wheel → spellbook screen (A casts) |
| Macros | Bound | Y / wheel Macros screen |
| Options / Set controls | Bound | Start; Options screen; Set controls wizard |
| Status (vitals) | Bound | Wheel Status screen (numbers) |
| Party list | Bound | Wheel Party screen (roster only) |
| World map (nearby) | Bound | Wheel World map screen |
| Loot corpse / open container | Bound | Radar use / double-click; loot dummy probe |
| Talk / speech | Partial | Chat input strip hidden in gamepad mode; no dedicated pad keyboard in-world yet; OSK is pregame |
| Context menus | Bound | Radar Y; backpack/paperdoll Back |
| Vendors (buy/sell) | Partial | Radar can use a vendor; no dedicated vendor pad screen |
| Banking | Partial | Radar can use a banker/bank box; no dedicated bank pad screen |
| Stealing / pickpocket | Partial | Skills screen can fire Stealing; target via radar/target cursor |
| Trade | Client only | Needs a second player; client can open trade, not a world pass alone |
| Party invite | Client only | Client opens a target; needs a second player to accept |
| Guild | Not yet | No pad screen; classic guild gump is mouse UI |
| Quests log | Not yet | Removed from the wheel (shard packet only) |
| Chat / messenger | Not yet | No pad screen |
| Housing / customization | Not yet | Mouse UI |
| Character creation / login | Bound | Pregame 3D pad path (`--pregame3d-probe`) |


## D-pad and chord map

What the pad does **today**, by context. A cell marked **not bound** has no
action. Holds that are not listed do not open a menu. This is the map; it is
not a promise of later chords.

### World (no screen, no wheel, no radar)

| Input | Tap | Hold | Right stick while held |
|---|---|---|---|
| D-pad up | Walk north | Keep walking north. Does **not** open a menu (**not bound**) | Pointer, as usual (the hold is not a mode) |
| D-pad down | Walk south | Keep walking south (**not bound** as a menu) | Pointer |
| D-pad left | Walk west | Keep walking west (**not bound** as a menu) | Pointer |
| D-pad right | Walk east | Keep walking east (**not bound** as a menu) | Pointer |
| Left stick | Same as the D-pad | Same | Pointer |

### LT — menu wheel

| Input | Tap | Hold | Right stick while held |
|---|---|---|---|
| LT | Reopen the last wheel window (under 0.2 s, stick centred) | The eight-slice wheel. Release opens the lit slice. Stick in the middle: nothing. B closes without opening | Points at a slice, same as the left stick. D-pad does **not** steer the wheel (**not bound**) |

Slices (default): up Backpack, up-right Paperdoll, right Journal, down-right Skills, down Spellbook, down-left World map, left Macros, up-left Options. Status and Party are offered in Set controls.

### A wheel window (backpack, paperdoll, journal, …)

The world camera is centred on the left half and still drawn under the
screen on the right half. On backpack the strap-cropped 9-slice fills that
half over the world (its transparent texels show the map); on paperdoll the
doll art does. Button hints sit on the left half, right-aligned to the bag's
left edge (or to the panel).

| Input | Tap | Hold | Right stick while held |
|---|---|---|---|
| D-pad up / down / left / right | Move the focus (a grid on backpack and paperdoll; a list elsewhere). Grid scrolls when the focus leaves the visible cells | Repeat is **not bound** (one step per press). Left stick repeats | **Not bound** (pointer is off while the screen is up) |
| A | Use / select the focus | — | — |
| B | Close the screen (camera returns) | — | — |
| X | Backpack: drop. Paperdoll: unequip to the pack. Other screens: **not bound** | — | — |
| Y | Backpack / paperdoll: equip / use. Other screens: **not bound** (does not open the macro row) | — | — |
| Back | Backpack / paperdoll: context menu. Other screens: **not bound** | — | — |
| LB / RB | Previous / next page, where the screen has pages | — | — |

### RT — interact radar

| Input | Tap | Hold | Right stick while held |
|---|---|---|---|
| RT | — | Radar of things in range. Release with a choice and no button: use it. Release with nothing chosen: nothing | Snaps the choice toward the push. LB / RB step. A use, X look, Y context menu, B close. Left stick still walks |

### Other chords (not D-pad)

| Input | What it does | Not bound |
|---|---|---|
| L3 | Always run | — |
| R3 | War mode. After Set controls moves war mode, R3 is the macro row. **LB + R3** cycles debug UI scale instead (0.5, 0.75, 1, 2, 3, 4) and does not fire R3's job | D-pad is not part of the chord |
| Start | Options | — |
| X (world) | Attack last | — |
| Y (world) | Macro row, or the Macros screen | — |
| LB / RB (world) | Target last / next hostile | — |
| A / B (world) | Use at the pointer / cancel | — |

The character-swapper flyout (below) is **not bound** in any of these contexts.

## Note for Moshu — multibox / character swapper

One UO connection is one character. Multibox means **several clients**, not a zoomed-out view of one world. Do not build a fake camera pull-back for this.

The engine (not this client pass) would need, for the player's other characters:

- a list, each with position
- what they are doing now
- auto-behavior toggles
- assume control
- spectate

On the controller we **reserve** these jobs for that flyout. They are names only. They are not in Set controls, they do nothing in game, and they are **not** a world-test pass:

| Reserved job | Meant to do | Status |
|---|---|---|
| Open swapper | Open the flyout of your other characters | **Not bound.** No engine list |
| Move across paperdolls | D-pad / stick moves the highlight | **Not bound** |
| Change auto behavior | Cycle that character's auto-behavior | **Not bound.** No engine toggle |
| Control | Assume control of the highlighted character | **Not bound.** Needs another client/connection |
| Spectate | Watch the highlighted character | **Not bound.** Needs the engine |


## Planned (spec — not bound yet)

This section is the controller backlog design. It is **not** a claim of what
works today. Bound behaviour is above; when something here ships, move it up
and keep both lists honest.

### Research basis

Solo PvM/PvP essentials from ClassicUO / official macro actions, common Razor
and UOSteam habits, and community guides:

| Rank | Action | Why solo needs it |
|---|---|---|
| 1 | Walk / run, war mode, attack last | Survive combat |
| 2 | Use / double-click, look, context menu | Interact with world |
| 3 | Target cursor answer (last, self, cycle, cancel) | Spells, skills, bandages, potions |
| 4 | Bandage self / last object | Heal without a mouse |
| 5 | Backpack + loot corpse | Inventory and post-fight |
| 6 | Paperdoll / status / skills | Character loop |
| 7 | Spellbook / last spell / hotbar macros | Casters and scripted chains |
| 8 | Journal + talk / speech | Server feedback and NPC speech |
| 9 | Pet commands (all follow/stop/stay/attack) | Tamers — speech payloads |
| 10 | Vendor / bank / trade / party / guild | Town and social — later |

Baldur's Gate 3 mapping (Moshu suggested looking here):

| BG3 idea | GUO analogue |
|---|---|
| LB/RB action radials (spells, items, attacks) | LT menu wheel + Y macro row; future role favorites radial |
| RT shortcut radial (map, journal, sheet) | Already on the LT wheel |
| Free cursor (L3) vs D-pad target cycle | Right stick pointer + LB/RB hostile cycle / radar |
| Context menu on X | Radar Y / backpack Back |
| Hold-to-search / interact A | Radar A use |

### Action → control map (planned)

| Action | Placement | Notes |
|---|---|---|
| Bandage self | Direct / chord or macro-row favorite | P0 for melee; not a dedicated face button yet |
| Last object / last spell | Macro row or hold-Y favorites | ClassicUO LastObject / LastSpell |
| Target cursor: confirm | RT radar A (partially bound) | Ground targets still need a reticle |
| Target cursor: self | Chord while targeting (planned) | Touch bar already has Self |
| Target cursor: last | LB while targeting (bound as Target last) | Keep |
| Target cursor: cancel | B (bound) | Keep |
| Pet all-follow / stop | Navigable screen or speech favorites | Speech, shard-specific |
| Loot all / grab | Backpack / corpse screen action | No assumed loot-all without shard check |
| Skill use | Skills screen A (bound) | Target via radar when asked |
| Journal | Wheel (bound) | Unread badge planned |
| Spellbook / hotbars | Wheel + Y macros (bound) | Role presets planned |
| Custom server gumps | **Generic focus-graph** (planned) | One navigator for all layouts |
| Text entry | Steam Deck OSK / existing fallback | Auto-open when a field focuses |

### Hard problems (honest)

1. **Target cursor.** Spells, skills and items put up a cursor that wants a
   mobile, an item, or a **ground tile**. Radar covers entities. Ground /
   field spells still need a reticle the right stick can place, with A to
   confirm and B to cancel. Do not pretend LB "Target last" answers every
   cursor.

2. **Server-sent custom gumps.** Arbitrary button / checkbox / text-entry /
   scroll layouts. **Started:** `PadGumpNav` builds a position-sorted focus
   list over mouse-accepting controls; D-pad moves, A activates (click at
   centre), B closes, LB/RB nudge scroll. Still needed: focus ring, text-entry
   OSK, and a live probe against a custom server gump. Virtual cursor remains
   the fallback when the graph cannot reach a control.

3. **Text entry.** Steam Deck floating keyboard when available; otherwise the
   existing OS / probe fallback. Never let typing fire combat bindings.

### Solo-first scope

Ship attack, use, loot, talk, context menu and pickpocket before trade, party
and guild. Those later systems stay "Client only" or "Not yet" until a second
player or a dedicated pad screen exists.

See `docs/controller_backlog.md` for the ranked backlog with acceptance checks.

## For developers


- The design record is ADR-0025 (`docs/architecture/ADR-0025-gamepad-on-by-default.md`).
- The code is `src/Input/Gamepad/GamepadInput.cs` and `src/Input/InputMode.cs`.
- The menu wheel, radar and Set controls are `src/Input/Gamepad/PadWheel.cs`,
  `PadRadar.cs`, `PadWizard.cs`; the action map is `PadBindings.cs`; their
  layer and art are `PadOverlay.cs`. Set controls ports the behaviour of
  Ghostroads' own "Set controls" (`game/ui/set_controls.gd`).
- `--gamepad-probe` checks the bindings with scripted pad events, and
  `--pad-wheels-probe` the wheel, the radar, the new buttons, Set controls and
  the trackpad, with a picture at each step (see
  [Scripted Runs and Probes](Scripted-Runs-and-Probes.md)).
