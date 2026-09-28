# Tall gumps on handhelds (C11)

Which gumps do not fit a handheld's screen, or fit only at a size too small
to use. For each: the problem on one screen and on two, and a recommended
treatment. Options already has its treatment (C11: fitted, drawn over the
bar).

## How it was measured

- **Client gumps:** opened in the game and their size logged
  (`--ui-gallery`; lines `[GUO] gump size:`).
- **Containers, books, shop lists and the bulletin board:** the size of the
  gump art the client draws them on, from the owner's install
  (`tools/uopack unpack`).
- **Shard gumps:** the largest background in the ModernUO source
  (`tools/modernuo/src`); ServUO's are the same classes.

All sizes are in client pixels at 100%.

## The screens

| | Screen (px) | Command bar | Usable height | At the default scale 1.11 | At 2x |
|---|---|---|---|---|---|
| Odin 2 Mini | 1920 × 1080 | 1 row: 174 px; 3 rows: 378 px | 906 px (702 with 3 rows) | 816 client px (632) | 453 client px (351) |
| Thor, top screen | 1920 × 1080 | as the Odin | as the Odin | as the Odin | as the Odin |
| Thor, lower screen (shelf) | 1240 × 1080 | none (the companion strip: 29 px at 2x, Classic mode only) | 1080 px | 1240 × 1080 client px (shelf scale 0) | 540 client px |

**The default scale.** The touch layer's screen scale is 1.11 on the Thor
(2 / 1.8, see `OptionsGump`); the Odin follows the same rule.

**The finger scale, 2x.** At 1.11 a UO gump's text is about 1 mm tall on
these panels, too small to read or hit. A gump is usable at about 2x (a
checkbox about 3 mm, a row about 4 mm). That is what Options is fitted to
(2.16x).

**The result.** At the default scale none of these gumps is taller than the
usable height. The problem is that, to be usable, most need about 2x, and
then several do not fit.

## The catalogue

"Fits at 2x" is measured against the one-row bar's 906 px, and in brackets
against three rows' 702 px.

| Gump | Size | Source | At 1.11 | Fits at 2x? | One screen (Odin, Thor top) | Two screens (Thor) | Treatment |
|---|---|---|---|---|---|---|---|
| **Options** | 700 × 499 | measured | 777 × 554 | 1512 × 1078: over the bar only | Tiny at 1.11; at 2x its bottom row is under the bar | Same on the top screen | **Done (C11):** full-height, fitted to the screen, drawn over the bar |
| **Macro editor** (MacroGump) | **1095** × 355 | measured | 1215 × 394 | Too wide: 2190 px | The widest gump; at 1.11 its text is unreadable | The lower screen is 1240 wide, so only 1.13x | **Scale to fit the width** (about 1.75x on 1920); keep on the top screen |
| **Party** (PartyGump) | 450 × 480 | measured | 500 × 533 | 960 tall: no (no) | Under the bar at any usable size | Fits the lower screen at 2x (960 < 1080) | **Move to the lower screen**; on one screen **full-height** (over the bar) |
| **Skills, advanced** | 500 × 360 | measured | 555 × 400 | 720: yes (no) | Fine with one row; under three rows | Fits the lower screen at 2x | **Scale to fit** above the rows; **lower screen** on the Thor |
| **Skills, standard** | 345 × 294 | measured | 383 × 326 | 588: yes (yes) | Fine, and its list scrolls | Fine | Scale (pinch); nothing new needed |
| **Journal** | 345 × 298, resizable | measured | 383 × 331 | 596: yes (yes) | A player can drag it taller than the screen | Companion tabs (C7) hold it | **Scroll** (it does); cap its resize at the usable height; **lower screen** or companion tabs |
| **World map** | 400 × 400, resizable | measured | 444 × 444 | 800: yes (no) | Map players want it big | Lower screen, or full-height | **Full-height** (over the bar) with pinch zoom inside; lower screen on the Thor |
| **Radar map** | 200 × 200 | measured | 222 × 222 | yes | Fine | Fine | Nothing |
| **Abilities book**, spellbooks (0x08AC, 0x2B00…) | 406 × 229 | measured / art | 451 × 254 | 458: yes (yes) | Fine | Fine | Scale (pinch) |
| **Paperdoll** | 262 × 324 | art 0x07D0 | 291 × 360 | 648: yes (yes) | Fine | Shelved by default | Nothing new |
| **Status** | at most 577 × 216 (art 0x2A6C; the classic bar is smaller) | art | ≤ 640 × 240 | yes | Fine | Shelved by default | Nothing |
| **Containers**: backpack 230 × 204, bank box 180 × 240, the largest chest 258 × 211 | as listed | art 0x003C–0x0052 | ≤ 286 × 266 | yes | Fine | Fine | Nothing (grid view: its own scroll) |
| **Grid containers** (GUO) | grows with the items | – | – | a full bank: no | A full bank grid runs under the bar | Lower screen | **Scroll** inside (cap at the usable height); **lower screen** |
| **Vendor buy / sell** (ShopGump) | 283 × 307 art, list scrolls | art 0x0870 / 0x0872 | 314 × 341 | 614: yes (yes) | Fine; the list scrolls | Fine | Scale (pinch) |
| **Bulletin board** | 490 × 410 | art 0x087A | 544 × 455 | 820: yes (no) | Under three rows | Fits the lower screen | **Scale to fit**; **lower screen** on the Thor |
| **Guild** (client button) | 500 × 300 | measured | 555 × 333 | yes | Fine | Fine | Nothing |
| **Guild** (shard, ModernUO BaseGuildGump) | 600 × 440 | shard source | 666 × 488 | 880: just (no) | Under three rows | Lower screen: 1200 wide at 2x, fits | **Scale to fit**; **lower screen** |
| **House** (AOS 420 × 440, classic 420 × 430) | as listed | shard source | 466 × 488 | 880: just (no) | Under three rows | Fits the lower screen | **Scale to fit**; **lower screen** |
| **Craft** (tools, CraftGump) | 530 × 437 | shard source | 588 × 485 | 874: just (no) | Used constantly by crafters; under three rows | Lower screen | **Lower screen** (shelve like the paperdoll); one screen: **scale to fit** |
| **Help / page a GM** (HelpGump) | 540 × up to ~500 (built by entry count) | shard source | up to 599 × 555 | no, when long | Its bottom under the bar when long | Lower screen | **Full-height** (it is modal-like and short-lived) |
| **Admin** (staff, AdminGump) | 420 × 480 | shard source | 466 × 533 | 960: no | Staff only | Lower screen | **Full-height** |
| **Resurrect** | 400 × 350 | shard source | 444 × 388 | yes (yes) | Fine | Fine | Nothing |
| **Report murderer** | 320 × 290 | shard source | 355 × 322 | yes | Fine | Fine | Nothing |
| **Character statue** | 327 × 324 | shard source | 363 × 360 | yes | Fine | Fine | Nothing |
| **Other shard gumps** (quests, moongates, runebooks, [props, [add) | usually ≤ 540 × 480 | shard source | ≤ 600 × 533 | mostly | A generic shard gump can be any size | Lower screen | **Generic rule**, below |

## What to build next, in order

1. **Full-height for more gumps.** `GumpPresentation.IsFullHeight` is one
   line per type. Add the world map, the party gump, the help gump and the
   admin gump. They are fitted to the screen on open and drawn over the bar,
   as Options is.
2. **Scale to fit above the rows.** When a supported gump opens taller than
   the room above the open rows, fit it to that room, instead of leaving it
   at the top overlapping the rows (C8's rule). This covers the advanced
   skills, bulletin board, guild, house and craft gumps.
3. **Shelve the crafters' and players' gumps on the Thor.** Shelve the craft
   gump and the guild and house gumps to the lower screen (a new
   `DualScreenShelve*` default), as the paperdoll is shelved.
4. **Cap resizable gumps.** Cap the journal and grid containers at the usable
   height, so a drag cannot make them taller than the screen.
5. **A generic rule for shard gumps.** A server gump whose height is over 90%
   of the usable height opens fitted, on the lower screen where there is one.
   Its reply IDs and switches are untouched: presentation only
   (`GumpPresentation` never changes what is sent to the server).
6. **The macro editor.** Fit it to the width (1.75x). A reflow would be a
   rewrite of an upstream gump, so it is left as it is.
