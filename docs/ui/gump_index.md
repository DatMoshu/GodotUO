# Gump index: mobile plan (ADR-0024)

Every gump in `godot/GUO/src/Game/UI/Gumps`, and the notable shard gumps, in
priority order. Each row gives its size, its mobile risk and its plan.

**Sizes** are in client pixels at 100%, and come from one of three places:

- measured in the game (`--ui-gallery`);
- read from the gump's code, where the size is a constant;
- taken from the gump art it is drawn on.

The measurements behind the risk column are in `tall_gumps.md`.

**Plans:**

- **Modern needed:** a Godot view in UoTheme (ADR-0024). Touch defaults to
  it; the desktop stays Classic.
- **Classic plus fit:** the ported gump, fitted to the screen or to the room
  above the bar (C11's full-height fit, or the fit-above-the-rows rule).
- **Classic fine:** small enough, or rare enough, to leave as it is. Pinch
  resizes it where supported.

**Risk:**

- **High:** unusable at the default scale, or does not fit once scaled to
  use.
- **Medium:** usable but dense, or only a problem with the bar's rows open.
- **Low:** fine.

## Modern needed, in build order

| # | Gump | Size | Risk | Why | Plan |
|---|---|---|---|---|---|
| 1 | **Options** (OptionsGump) | 700 × 499 | High | Dense mouse layout, 13 pages. C11 fits it at 2x; a thumb still needs bigger rows and lists | **Done: Modern, classic-styled** (the owner; ModernOptions, docs/ui/modern/options.md): the same page column and grey Options look, with finger-sized rows, touch lists and sliders, and a pinned button row. The settings it covers write the profile fields Classic's Apply writes; Classic view covers the rest |
| 2 | **Party** (PartyGump) | 450 × 480 | High | 960 tall at 2x: under the bar at any usable size | **Done: Modern** (ModernParty): member list with bars, Add/Remove/Leave/Loot as plates, one column |
| 3 | **Skills** (SkillGumpAdvanced 500 × 360; StandardSkillsGump 345 × 294) | as listed | High | 58 rows of 1 mm text, and tiny lock and up/down arrows | **Done: Modern** (ModernSkills, both classic skills gumps): a scrolling list with finger-sized rows, a tap to use, a lock toggle per row, and a group filter |
| 4 | **Macro editor** (MacroGump) | 260 × 200 panel (its bounds run from 0,0: the "1095 wide" first measured was that, not the panel) | Low | The fast-assign editor is small; the full macro list is Options' Macros page, whose rows are combo boxes | **Classic** for the fast editor. **Macros page done: Modern** (ModernMacros, a page of Modern Options): the macro list; add, delete and place a button; actions and their choices picked from finger-sized lists; text typed. The hotkey stays Classic |
| 5 | **World map** (WorldMapGump) | 400 × 400, resizable | High | Map players want it full screen; its markers and context menu are mouse-sized | **Done: Classic + fit + gestures** (agreed with the director): sized to the whole screen (it is resizable, so it draws more map, not bigger pixels), a pinch zooms the map and a drag pans it. A Godot rebuild would redo its map rendering for no gain; the map is a picture, not a form |
| 6 | **Spellbook** (SpellbookGump), abilities book (CombatBookGump), racial book | 406 × 229 | Medium | Small spell icons and page corners | **Done for spellbooks: Modern** (ModernSpellbook; Mastery books stay Classic): a spell grid (icon and name), a tap to cast, and a hold to place a spell button (the UseSpellButtonGump the desktop drag makes). **Abilities book done: Modern** (ModernAbilities): the weapon's two abilities as large tiles (tap to use; hold to place the UseAbilityButtonGump the book's drag makes), then every ability with its icon, its text (the book's tooltip cliloc) and its weapons (the book's own table). The **racial book** (RacialAbilitiesBookGump) **stays Classic** and pinch-scalable. It holds 4 to 6 passive racial traits, and its one action is the gargoyle's Flying toggle. A Modern copy would duplicate its private name tables for little gain |
| 7 | **Vendor buy / sell** (ShopGump) | 283 × 307 art, list scrolls | Medium | Tiny +/- and amount buttons | **For now: pinch-scalable** (in GumpPresentation.Supports). **Modern later**: the shard's packets create it, add it and keep filling it through `UIManager.GetGump<ShopGump>()`, and its item lists are private. A Modern view needs the classic kept alive but hidden, plus a PORT DEVIATION in ShopGump exposing its items and the add, remove, accept and clear actions. That is more than any other Modern gump's hook |
| 8 | **Bulletin board** (BulletinBoardGump) | 490 × 410 | Medium | Under three rows | Modern: a post list, then the post; or Classic plus fit |
| 9 | **Markers manager** (MarkersManagerGump) | 620 × 500 | Medium | Wide and tall, a dense list | **Done: Modern** (read and go): marker files as a stepper, search, and Go to per marker (WorldMapGump.GoToMarker). Edit and remove stay in its Classic view, which saves the file |
| 10 | **Journal** (JournalGump, ResizableJournal) | 345 × 298, resizable | Medium | Resizes past the screen | **Done: a reader, not a replacement** (ADR-0024, director 2026-09-28). The classic journal stays the always-open gump it is. **ModernJournal** is a full-height reader in large type, with the classic journal's four filters as plates (writing the same profile fields). A drag scrolls it back, and it follows new lines at the bottom. It opens from the journal's window menu ("Read") and from the bar's **Read Journal**, which is the journal slot's first hold alternate. Its lines are the companion tabs' reader (`JournalReader`), so one reader serves the Thor's second screen and one-screen devices |
| 11 | **Status** (StatusGumpBase) | ≤ 577 × 216 | Low | Fine; its Modern view exists as the companion tabs' Character page | Classic by default; companion Character where there is a second screen |

## Classic plus fit

| Gump | Size | Risk | Plan |
|---|---|---|---|
| **Help / page a GM** (shard: ModernUO HelpGump, type ID 0x7510FA8F) | 540 × up to ~500 | High when long | Shard gump: stays Classic (ADR-0024). **Full-height by type ID**, prototyped in the C11 follow-up and tested on the dev shard (fitted 1.93x) |
| **Admin** (shard: ModernUO AdminGump, 0xE37B54FE) | 420 × 480 | High (staff only) | As Help |
| **Craft** (shard CraftGump) | 530 × 437 | Medium | Fit above the rows; lower screen on the Thor |
| **House** (shard HouseGump / HouseGumpAOS) | 420 × 440 | Medium | Fit above the rows; lower screen |
| **Guild** (shard BaseGuildGump 600 × 440; the client's button gump 500 × 300) | as listed | Medium | Fit above the rows; lower screen |
| **Other shard gumps** (quests, moongates, runebooks, [props, [add) | usually ≤ 540 × 480 | varies | The generic rule in tall_gumps.md: over 90% of the room, fitted; lower screen where there is one |
| **House customization** (HouseCustomizationGump) | large (the house designer) | High, but rare | Classic plus fit; a touch design tool is out of scope |
| **Inspector** (InspectorGump, debug) | 500 × 400 | Medium | Classic plus fit (developer tool) |
| **Race change** (RaceChangeGump) | 595 × 400 | Medium, rare | Classic plus fit |
| **Character creation** (CharCreationGump and its pages) | 640 × 480 layouts; 470 × 372 panels | Medium, once per character | Classic plus fit (the login flow is already centred for touch, C-series) |

## Classic fine

| Gump | Size | Note |
|---|---|---|
| Paperdoll (PaperDollGump) | 262 × 324 | **Touch fit done.** On one touch screen it opens at up to 2x (no taller than 85% of the room above the bar). A size the player pinches or resizes it to is the size the next one opens at, but the automatic fit is not remembered, so it keeps following the screen. It is shelved on the Thor, and one reopened at login keeps its saved size (`GumpPresentation.FitPaperdolls`, touch probe) |
| Containers (ContainerGump) | ≤ 258 × 240 | Grid view (GridContainerGump) is the touch layout |
| Grid container, grid loot (GridContainerGump, GridLootGump) | grows with items | Scrolls; cap at the room (tall_gumps.md) |
| Radar map (MiniMapGump), map (MapGump) | 200 × 200 | |
| Trade (TradingGump) | small | |
| Health bars (HealthBarGump) | small | |
| Buffs, counters, info bar (BuffGump, CounterBarGump, InfoBarGump) | small strips | |
| Macro, spell, skill and ability buttons (MacroButtonGump, UseSpellButtonGump, SkillButtonGump, UseAbilityButtonGump, RacialAbilityButton) | single buttons | The command bar covers these on touch |
| Popup and context menus (PopupMenuGump), split stack (SplitMenuGump), questions and messages (QuestionGump, MessageBoxGump, TextEntryDialogGump, PartyInviteGump) | small, modal | |
| Chat (ChatGump, ChatGumpChooseName) | 220 × 200 | |
| Colour picker (ColorPickerGump) | small | Opened from Options: **Modern Options has its own** (ModernHuePicker, the same palette). The dye tub's picker (a shard's 0x95) stays Classic |
| Profile (ProfileGump), books (ModernBookGump), text container (TextContainerGump) | medium, scroll | |
| Ignore list (IgnoreManagerGump), user markers (UserMarkersGump) | ≤ 320 × 220 | |
| Location go (LocationGoGump), quest arrow, tip notice, name overhead, network stats, debug, credits, menu (MenuGump) | small | MenuGump's large constant is a scroll height, not its size |
| Login: login, server select, character select, loading (LoginGump, ServerSelectionGump, CharacterSelectionGump, LoadingGump) | ≤ 451 × 343 | Centred for touch already |
| GUO's own: the top bar (TopBarGump), the world view (WorldViewportGump), GumpLayoutGump (the second screen's pre-game card is a Godot card, not a gump) | – | Not gumps a player opens |

## Tonight (the owner's night task)

Build the Modern views above in order, each with a probe check and Thor and
Odin photos, and commit each gump in its own small batch:

1. Options (classic-styled): done;
2. Party: done;
3. Skills: done;
4. Macro editor: re-measured as small (see above), Classic;
5. World map: Classic + fit + gestures;
6. Spellbook: done.

**Status after the night of 2026-09-28:**

- **Modern views, done:** Options, Party, Skills, the spellbooks, the
  abilities book and the markers manager, plus the journal reader.
- **Options pages, done:** General, Sound, Video, Macros, Tooltip, Fonts,
  Speech, Combat, Containers and Touch.
- **Classic, fitted:** the world map, the paperdoll and the racial book stay
  Classic, fitted to the screen, with gestures where they help.
- **Tests:** the touch probe passes 111/111 on the desktop. On the Thor, the
  companion Journal tab passed drag back and following new lines; opening at
  the newest line is fixed and waits for its device recheck.
- **Open:** the bulletin board and the shop wait on the owner's decision
  (`modern/board_and_shop_note.md`). The Options colours now have a Modern
  picker; the font pickers stay Classic.

Help and Admin are shard gumps. By ADR-0024 they stay Classic, with the
type-ID full-height fit as their treatment.
