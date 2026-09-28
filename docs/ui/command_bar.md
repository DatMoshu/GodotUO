# The command bar (C8): design

The touch bar at the bottom of the screen on handhelds. It replaces the
two-bar layout that had a chevron between the bars. This page is the design,
reviewed with the frontend-design skill before it was built. The code is
`src/Input/Touch/TouchGumpBar.cs`.

The desktop never shows it. The desktop keeps ClassicUO's top bar, 1:1.

## Subject, audience, job

- **Subject:** Ultima Online played on a handheld: the AYN Thor (a 1920x1080
  main screen plus a 1240x1080 lower screen) and the Odin 2 Mini (one
  1920x1080 screen).
- **Audience:** UO players who know the 1997 client by heart.
- **Job:** thirty of the commands a keyboard player has on hotkeys, one thumb
  away, without covering the world until they are asked for.

## Plan

### Colour

The bar has no colours of its own. Everything comes from UO's art or UO's
hues.

| Name | Value | Use |
|---|---|---|
| Band | black at 55% | Behind the rows, so the plates read over any terrain |
| Plate | gump 0x098D, as drawn | Every button |
| Ink | font 1's own near-black | Captions |
| Lit | UO gold #E0B050 (top bar hover hue 0x0036) | The caption of a button under a finger, or just run |
| Innocent / enemy / murderer… | notoriety hues | The target's name |
| Hits / mana / stamina | #B8483E / #3F6FC4 / #C9A23B | The target strips (the companion tabs use the same three) |

### Type

UO unicode font 1, the client's own, through the same FontsLoader call
RenderedText makes. There is no other face. Captions are drawn at 2x.

Measured in the owner's install (`unifont1.mul`), one line at 2x fits
158 px. Every default caption fits except `Nearest Hostile` (180 px). It
shows as `Nearest Foe` (142). `All Follow Me` (158) just fits.

On a narrower view the plates shrink: at `s` = 2 (a 1240x1080 screen, the size of
the Thor's lower panel) a plate is about 110 px and holds about 95 px
of caption (measured on the emulator). A
caption that does not fit is cut at a word with `...` after it, as the
abilities book cuts its rows, because the UO font has no `…`
(`Last Target` shows as `Last...`). One word that is still too wide shows
its start, clipped at the plate's inner edge (`TouchGumpBar.FitCaption`).

### Layout

The pixel grid is the plate's art scale, `s`: the largest whole number at
which ten cells fit across the view. It is 3 on both handhelds.

| Part | Size at 1920 wide |
|---|---|
| Cell | 1/10 of the width: 192 px |
| Plate | cell less a 16 px gap, by 23 art px × s: 176 × 69. The plate is cropped from its middle, never stretched |
| Row | the plate plus 11 art px of padding: 34 × s = 102 px (7.1 mm on the Thor, 5.9 mm on the Odin). The hit target is the whole cell, 192 × 102 |
| Handle strip | 24 art px × s = 72 px. It holds the chips on the left, the target in the centre and the arrow on the right |
| Arrow tab | a 0x098B plate cropped to 42 art px (126 × 69 at 3x), centred over the last column. The gold arrow 0x0983 (up) / 0x0985 (down) sits in it at s |
| Target strips | 2 art px tall each, 1 art px apart, framed; as wide as the name, 40 to 80 art px |

```
 ┌──────────────────────────── handle strip ─────────────────────────────┐
 │  [chips…]              Sea Horse                          ╭─────╮     │
 │                        ▬▬▬▬▬▬▬▬▬▬ hits                    │  ▲  │     │
 ├────────────────────────────────────────────────────────────╰─────╯────┤
 │ row 3  Skills  Spellbook  All Names  Open Door  …           Options  │  slides in 3rd
 │ row 2  Next T  Last Obj   Heal Pot   Cure Pot   …           Status   │  slides in 2nd
 │ row 1  Paperdoll Backpack Journal Map Chat War Nearest …   Bandage  │  always; never moves
 └───────────────────────────────────────────────────────────── screen edge
```

**Row 1 is anchored to the bottom edge and never moves.** New rows open
above it, under the handle. The handle rises with them. The director's
proposal reads top-down as "chevron, then rows 1–3". This keeps that order
for the handle, but puts row 1 where the thumb rests: the most-used row
never slides out from under a thumb. Pulling the handle up reveals what is
under it, as a sheet does. It is also what fixes the Odin bug: the rows under
a finger never move when the handle is used.

The target header is centred in the handle strip. The chips sit left in the
same strip: on the Odin that is the tray, which exists only while something
is minimised. On the Thor, chips go to the lower screen instead.

### States

| State | Plate | Caption |
|---|---|---|
| Idle | as drawn | ink |
| Held (finger on it, nothing run yet) | drawn 1 art px lower, darkened 30% | gold |
| Lit (just run, 140 ms) | as drawn | gold |
| Targeting | Chat → **Self**, War/Peace → **Cancel**, in place | Self in innocent blue, Cancel in murderer red |
| War | the War/Peace button reads `Peace` | ink. The target header's strip frame turns red while at war |

There are no translucent colour washes over plates (the old bar used them for
Self, Cancel and the macro row). A hue on the caption says the same thing,
the way UO itself says it.

### Motion

- **Height.** The bar's height is continuous, from one row to three. Rows
  are revealed by the rising handle and clipped under it.
- **Tap the arrow.** At one row, it opens two. At two or three, it closes
  back to one.
- **Hold and drag the arrow or the empty strip.** The height follows the
  finger 1:1. Past three rows or below one, it moves at 0.3× (rubber band,
  at most 30 px).
- **Release.** A flick faster than 0.5 px/ms goes to three rows upward and to
  one row downward. A slower release snaps to the nearest row count.
- **Settle.** The snap is 200 ms, ease-out-back with about 6% overshoot.
  With the Options toggle "Reduce motion" on, it snaps at once.
- **Vibration.** 15 ms on each snap, and on each row boundary crossed while
  dragging. It uses the Options toggle "Vibrate on snap", which is off by
  default.
- **War.** Entering War mode opens two rows unless the player closed the
  rows since entering the world, as today.

### Handle affordance and the grab zone

The arrow tab is the handle. Its grab zone is the tab, widened by one gap on
each side and extended to the strip's full height. It covers no command, so
a grab there cannot run one. The rows never take the gesture.

The strip has no band, and gumps are not kept above it; only the open rows
are reserved. The first build reserved the whole strip and made all of it a
grab zone. On the Odin that put Options' Cancel/Apply row under the strip:
it fit under the old bar, but not above the strip. The target header carries
its own small backing, and the chips have their plates.

With three rows open, a gump taller than the room left above them (Options
on the Odin) is kept at the top of the screen, and its bottom sits under the
rows until they close. This is open for the owner: the alternative is to fit
such a gump to the room, which changes its size.

## Principles

1. **One pixel language.** UO art at a whole-number scale, UO's font, UO's
   hues, nearest-neighbour. Nothing is smoothed and nothing is Godot-themed.
2. **Nothing moves under the thumb.** Row 1 is fixed, the targeting swap
   happens in place, and buttons act on release.
3. **One loud thing: the target.** The name in its notoriety colour is the
   only coloured text on the bar. Attack Last is never a guess.

## Review against the brief

- *Generic?* The first draft had icons on parchment tiles above the plates.
  That is a mobile-app idiom, and it took 26 art px of height for art that
  covers only half the commands (there is no item for "All Names"). Dropped:
  plates with UO captions read as the top bar's own buttons, which is what the
  owner asked for ("small gump buttons, or scale that button").
- *Mixed scale:* the plates are at 3x and the captions at 2x. No caption fits
  a plate at 3x (at most 52 art px). The paperdoll's jewel buttons have text
  finer than their frame too, so this is within the look.
- *Colour washes removed:* the old bar tinted the macro row warm and the
  Self and Cancel buttons blue and red. Those were our inventions. A caption
  hue is UO's own way to mark state.

## Rows (defaults; every slot editable in Options, saved by action name)

| Row | Slots |
|---|---|
| 1 | Paperdoll, Backpack, Journal, Map, Chat, War/Peace, Nearest Foe, Attack Last, Last Target, Bandage Self |
| 2 | Next Target, Last Object, Heal Potion, Cure Potion, Ability 1, Ability 2, Last Spell, Last Skill, Arm/Disarm, Status |
| 3 | Skills, Spellbook, All Names, Open Door, All Follow Me, All Stop, Bank, Guards, Party, Options |

Each slot runs upstream's own macro or `GameActions` call, as a macro button
does:

- SelectNearest (Hostile), TargetNext, AttackLast, LastTarget, LastObject,
  BandageSelf, WarPeace;
- UsePotion (BestHealPotion, BestCurePotion);
- PrimaryAbility, SecondaryAbility, LastSpell, LastSkill, ArmDisarm,
  AllNames, OpenDoor;
- Open (Status, Skills, MageSpellbook, PartyManifest);
- Say, for the speech slots. Their words are editable in the profile
  (per character and shard): `bank`, `guards`, `all follow me`, `all stop`.

## The hold popup and the slot editor (C10)

Holding a bar button still for 450 ms (`TouchInput.BarPopupMs`) opens three
buttons, stacked straight above it so a thumb slides up to them. From the
bottom they are the slot's first alternate, its second, and Edit.

- Letting go on an alternate runs it. Letting go anywhere else runs nothing.
- A tap is unchanged, and so is a finger that slides off before 450 ms.
- The popup ticks when it opens, if "Vibrate when the command bar snaps" is on.
- A plate with alternates carries a corner mark: three gold steps on an ink
  square, top right.

The alternates are variations of the slot's command, or commands the thirty
slots don't cover. Every slot starts with two, from
`BarCatalogue.DefaultAlternates`, which follows the owner's examples:

- Nearest Hostile → Nearest Party, Next Hostile;
- Heal potion → Cure, Refresh;
- Attack Last → Attack Selected.

Edit opens the slot editor, a stone card in the UO style
(`uo_godot_style.md`):

- It has three pickers: the tap and the two holds.
- The catalogue appears in seven groups, with a search over every action.
- A plate grid lists the actions, with "None" for an alternate.
- A speech action's words are editable.
- Save stores the slot by action name (`TouchBarSlots`, `TouchBarAlts`, and
  `TouchBarWords` for the words).

The catalogue (`BarCatalogue.All`) is about seventy upstream actions:

- windows;
- targeting, including the Select Nearest/Next/Previous scans with Hostile,
  Party and Follower;
- combat;
- healing: bandages and each best potion;
- magic and skills: a few spells and skills;
- pets and speech;
- other: doors, bow, salute, zoom, always run, buff icons.

Each action has a full title for the editor and a short caption that fits a
plate.
