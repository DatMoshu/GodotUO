# Mobile playability: what a player can do on a phone today

Status: assessment, 2026-09-26, branch `work/ui` at 1165190 (on top of
`work/android` 267d059). Nothing in the "proposed" column is built.

Measured on an AYN Thor (Android 13, 1920x1080, 960x540 client at total
scale 2) against the local ModernUO dev shard, account `guoprobe`, a GM.

**Mockup:** none. Codex was asked for wireframes (msg-005 in
`codex_message.md`) and was nudged (msg-006); no reply. Every proposal below
is text only.

## How to read the marks

| Mark | Meaning |
|---|---|
| **verified** | done by touch on the Thor, with a screenshot or log line named |
| **written** | the touch path exists (a tap is a left click, a double-tap a double click, a drag a drag, a long-press over a gump a right click), and the desktop touch probe or code reading says it should work, but nobody has done it on the device |
| **missing** | no way to do it on a phone without a keyboard or mouse |

The touch layer's gestures are in ADR-0017 section 5 and the header of
`src/Input/Touch/TouchInput.cs`. The mapping that matters here: a
**tap** is a left click. **Two taps within 350 ms** are a double click.
**Hold or swipe on the world** is the held right button, which walks
(and runs past 190 px). **Hold on an item** drags it. A **long-press over
a gump** is a right click. **Two fingers** zoom. The **gump bar** runs
along the bottom: Character, Inventory, Journal, Map, Chat, Options.

## The list

| Activity | Mark | Evidence, or what is in the way |
|---|---|---|
| Log in (account, password, shard, character) | **verified** | android run table `k3_*`, `t11_kb_pan.png`; recipe in memory `android-thor-device` |
| Walk and run | **verified** | hold-to-walk, android run table ("Hold-to-walk") |
| Zoom | **verified** | pinch, `t22_pinch_in.png` / `t23_pinch_out.png`; kept across relaunch |
| Open the backpack, see what is in it | **verified** | grid gump, `thor_grid_two.png`; slots are 88 physical px |
| Move items between containers | **verified** | 1000 gold dragged from the backpack grid to the bank grid through the split menu, `thor_grid_drag.png`, `thor_grid_dropped.png` |
| Use an item (double-click) | **verified** | double-tap on a book in the grid opened it, `thor_grid_doubletap.png` |
| Item names and tooltips | **verified** (gump) / **written** (world) | long-press on a grid slot showed the tooltip, `thor_grid_longpress.png`; a tap on a world item should print its name (untested) |
| Say something | **verified** | Chat on the gump bar, typed, check mark: said overhead and in the journal, `thor_chat_typed.png`, `thor_chat_sent.png` |
| Party, guild, alliance, emote, whisper, yell | **written** | `SystemChatControl`'s prefixes: `/` party, `\` guild, `\|` alliance, `: ` emote, `; ` whisper, `! ` yell, typed from the e-mail keyboard's symbol page; untested |
| Options | **verified** | Options on the gump bar, sections by tap, sliders by drag, Apply and OK, `thor_slider_*.png` (after fix cafea79) |
| Paperdoll, status, skills | **written** | Character on the gump bar opens the paperdoll (desktop touch probe); its STATUS and SKILLS buttons are taps. The paperdoll restored at login is verified clear of the character, `thor_restored_clear.png` |
| Equip and unequip | **written** | drag between the grid and the paperdoll's slots; the same GameActions as the mouse; untested on the device |
| **Fight:** war mode | **written** | the paperdoll's PEACE/WAR button is a tap. The hotkey (Tab) is **missing** |
| **Fight:** attack | **written** | in war mode a double-tap on a mobile attacks (double click); the double-tap on a world object is unverified on the device (android README) |
| **Fight:** attack last, last target, target next, bandage self | **missing** | all are keyboard macros (`AttackLast`, `LastTarget`, `TargetNext`, `BandageSelf`) |
| **Target:** answer a target cursor | **written** | a tap on the object is the left click that targets it. The probe's `[bank` target was sent from code, not by a tap |
| **Target:** target self | **written** | a tap on your own character, when the character is not under a gump |
| **Target:** cancel a target cursor | **missing** | only Esc cancels (`GameSceneInputHandler`); a long-press on the world is walking |
| **Cast:** from the spellbook | **written** | double-tap the spellbook (verified for a book) opens it; a double-tap on a spell's icon casts it, then target as above |
| **Cast:** spell icons on screen | **written** | drag a spell's icon out of the book to make a `UseSpellButtonGump`; a tap casts. Untested |
| **Macros:** run one | **written** | Options, Macros: a macro can be dragged out as a `MacroButtonGump`, and a tap runs it. Making the macro needs its name typed, which the IME does. Untested |
| **Macros:** hotkeys | **missing** | every macro is bound to a key; a phone has none |
| **Trade** with a player | **written** | drag an item onto the other player to open the secure trade gump; the accept boxes are taps. Needs a second client; `multi_client.bat` could drive one |
| **Vendors:** buy and sell | **written** | a tap on a mobile opens its context menu when the shard enables popups (`DelayedObjectClickManager`; ModernUO does). Buy/Sell open the shop gump: tap an item to add it, tap accept. Untested on the device |
| **Corpses:** loot | **written** | a double-tap on a corpse opens it; this profile's `GridLootType` is 2, so the grid loot gump opens too; a tap on an item loots it. The Options "Auto Open Corpses" works without a tap. Untested on the device |
| Pick up from the ground, drop on the ground | **written** | a hold on a world item drags it (touch probe); a drop on open ground. Untested on the device |
| Read the journal and the world map | **written** | Journal and Map on the gump bar; drag scrolls. Untested |
| Log out | **written** | the paperdoll's LOG OUT button |

**Summary.** A phone player can already log in, walk, zoom, manage their
bags and bank, use items, talk, and change options. Fighting, targeting,
casting, trading, vendors and looting all have a touch path, but it is
untested on the device. The real gaps are the keyboard-only actions:
cancelling a target, war mode by hotkey, and every macro hotkey (attack
last, last target, target next, bandage self).

## Proposed touch design for each missing item

None of these is built. Each would be its own commit, verified on the Thor,
behind a profile flag with a mobile default (the `since` mechanism). None
moves the world viewport. Profile version and Options are shared with the
canvas-background work, so the version bump waits for it (see "Order").

### 1. Cancel a target cursor

While `TargetManager.IsTargeting`, the gump bar's last two buttons change
to **Self** and **Cancel**, and change back when targeting ends:
- **Self** answers with the player (`Target(Player.Serial)`), which also
  covers the case where the character is under a gump.
- **Cancel** calls `CancelTarget()`.

The bar is a CanvasLayer owned by the touch layer, so no ported file
changes. Cost: small. Risk: a player tapping Options mid-target gets
Cancel. The swap is visible, and the bar button's label says which it is.

### 2. War mode, attack last, last target, bandage self: an action row

A row of up to six square buttons just above the gump bar. It is hidden
by default and shown by a seventh, small toggle at the bar's left end.
Each button holds one macro from the player's MacroManager, and a tap runs
it the way `MacroButtonGump.RunMacro` does (`SetMacroToExecute`, then
`Update`).

Defaults for a new mobile profile, each an existing `MacroType`:
- **War/Peace** (`WarPeace`)
- **Attack last** (`AttackLast`)
- **Last target** (`LastTarget`)
- **Target next** (`TargetNext`)
- **Bandage self** (`BandageSelf`)
- **Last spell** (`LastSpell`)

The row is reassigned in Options, Macros, with a new "put on the touch
row" button next to upstream's "create macro button". One marked hunk.

This covers every missing fight and macro line above with one control.
The action row is the mobile version of the hotkey. Upstream's floating
macro buttons stay available for players who want them anywhere.

The row takes about 1/12 of the screen height when shown. Placement's
`UsableArea` already reads the bar's `ReservedFraction`, so it would
count the row too.

### 3. Macro hotkeys in general

No gesture binding; the action row above replaces hotkeys on a phone.
ADR-0017 listed "gesture-to-macro binding" as not done. I recommend
against it: gestures are hard to discover and they collide with walk,
pick up and zoom.

## Order I would build it in

1. Target Self/Cancel on the bar: the only gap that can strand a player
   (a target cursor with no way out).
2. The action row, with the six defaults.
3. A device pass over the **written** lines. Those are the corpse, vendor,
   trade, cast, equip and ground-item lines. It needs a second client for
   trade and a monster or a spawned vendor on the shard; the probe
   accounts are GMs, so `[add` can place them.

Items 1 and 2 add profile keys, so they take the next profile version
after the canvas-background branch's. Neither touches the Display section
of OptionsGump.

## Known limits of what is verified

- Gumps draw over the chat line where they overlap it: the journal covered
  the right half of the typed text in `thor_chat_typed.png`. The line
  itself is above the keyboard.
- At grid slot size 80 two containers still fit side by side (five
  columns each) but they overlap the journal and paperdoll by about a
  quarter each, `thor_slider2.png`.
- The IME is Gboard on the Thor. Another keyboard may treat the e-mail
  type differently.
- All evidence is under `build/ui/2026-09-26/` in this worktree and in the
  android worktree's `build/android/` (gitignored, not in the repo).
