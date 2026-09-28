# Modern Options: design (ADR-0024, gump 1)

Reviewed with the frontend-design skill before it was built.

## Subject, audience, job

- **Subject:** ClassicUO's Options window, on a handheld.
- **Audience:** a UO player with a thumb, who knows the classic window's look.
- **Job:** change the settings a player changes, without a mouse; everything
  else is one tap away in the classic window.

## Plan

**Look: the classic Options, not a new card.** The owner asked for the
classic style, and the classic window is not a stone gump. It is a
near-black translucent panel (AlphaBlendControl, 95%), text in font 1, thin
grey rules, a page column on the left, and the jewelled UO buttons at the
bottom. Modern keeps exactly those materials and makes them thumb-sized.

| Token | Value | Classic source |
|---|---|---|
| Panel | `#0c0c0c` at 95% | AlphaBlendControl, hue 999 |
| Rule | `#6c6c6c`, 1 art px | `Line` controls, Color.Gray |
| Text | cream `#eeeade` | font 1, hue 0xFFFF on the dark panel |
| Heading | grey `#9c9c9c` | the section titles |
| Selected page | `#3a3a3a` bar, white text | NiceButton's selected state |
| Lit | gold `#e0b050` | UoTheme's lit |

- **Type:** font 1 only (UoTheme.Font): 1x for rows, 2x for the page's title.
- **Art:** UO's check box (0x00D2 / 0x00D3), slider (0x00D5–0x00D8) and marble
  plates, from UoTheme. The footer uses the classic window's own buttons:
  Cancel 0x00F3, Apply 0x00EF, Default 0x00F6, Okay 0x00F9.

**Layout:** fitted full-height below the top bar, as C11 fits the classic
window. At art scale 3 on 1920 × 1080 that is about 620 × 330 art px.

```
 ┌──────────────┬──────────────────────────────────────────────┐
 │ General      │  Sound                                        │  title 2x
 │ Sound  ◀sel  │ ─────────────────────────────────────────────│
 │ Video        │  [x] Sounds                                   │  row 26 art px
 │ Containers   │      Volume ════════════●════  80             │  (78 px, 5.4 mm Thor)
 │ Touch        │  [x] Music                                    │
 │              │      Volume ═══════●═══════  55               │  scrolls with a swipe
 │              │  [x] Footsteps                                │
 │──────────────│  [ ] Combat music                             │
 │ Classic view │                                               │
 ├──────────────┴──────────────────────────────────────────────┤
 │         [CANCEL]   [APPLY]   [DEFAULT]   [OKAY]                │  pinned footer
 └──────────────────────────────────────────────────────────────┘
```

**Rows:** every row is one control the full width of the content, 26 art px
tall, and all of it is the target.

- **A check box row:** the box and its words. A tap anywhere on the row
  toggles it.
- **A slider row:** the words, the UO slider across the rest, and its value.
  A drag that starts on the slider moves it; a drag anywhere else scrolls
  the page.
- **A choice row:** the words, then `‹  value  ›` as two small plates. A
  tap steps through the choices. That is fewer taps than a list for two to
  nine choices, and there is no popup to aim at.

**Pages it covers first** (the settings a player changes on a phone):

- **General:**
  - highlight objects, pathfinding;
  - always run, unless hidden;
  - auto-open doors, smooth doors;
  - auto-open corpses;
  - mobiles' HP, highlight poisoned;
  - names overhead.
- **Sound:** sounds and their volume, music and its volume, footsteps,
  combat music.
- **Video:**
  - hide roofs, trees to stumps, hide vegetation;
  - circle of transparency;
  - shadows;
  - death screen.
- **Macros** (ModernMacros), a page of its own kind, in three views:
  - **the list:** a name field and Add, then a row per macro with Edit and
    Place button;
  - **one macro:** its key (shown, not edited, since a phone has no keys;
    Classic view sets it), then its actions. Each action is a plate naming
    its type, with its choice or its text under it, and Remove. Then come
    Add action and Place button. Delete asks for a second tap;
  - **a picker:** a finger-sized list of the action types, or of one
    type's choices, the current one lit.

  The classic page does the same with combo boxes. Edits are live on the
  macros, as there, and Apply or Okay saves them (`World.Macros.Save()`, as
  the classic Apply does). Place button makes the MacroButtonGump that the
  classic list's drag makes.
- **Tooltip:** use tooltips; the delay, the zoom and the background
  opacity, as sliders over the classic's ranges.
- **Fonts:** override the game font, as ASCII or Unicode; force Unicode in
  the journal.
- **Speech:**
  - scale the speech delay, and the delay itself;
  - save the journal to a file;
  - chat opens on Enter (switching the system chat as the classic Apply
    does);
  - hide the chat gradient;
  - ignore guild or alliance messages;
  - party messages overhead.

  The hues and the font pickers on these pages stay in Classic view: they
  need a colour picker and a font preview of their own.
- **Combat:** ask before a criminal act, or a criminal beneficial one; cast
  spells by one click; buff bar timers; fast spell assign; colour spells by
  kind; show DPS with damage. The notoriety and spell hues stay in Classic
  view.
- **Containers:** grid view, grid slot size.
- **Touch:**
  - vibrate when the bar snaps;
  - reduce motion;
  - hold-and-flick (four choices);
  - "Edit the command bar" (the slot editor).

The pages follow the classic's order. The page column scrolls with a drag
where ten pages outgrow a short screen.

**The escape:** "Classic view", pinned at the foot of the page column, opens the
ported Options, fitted, for everything Modern does not show. A page's
Default puts that page's covered settings back to the profile's defaults.

**The contract:** Apply and Okay write each covered setting to the same
profile field the classic Apply writes, with the same side effects:

- the audio volume, and stopping sound or music;
- redrawing the roofs;
- cleaning the tree textures.

Cancel writes nothing.

## Review against the brief

- **Generic?** A first thought was UoTheme's stone card, like the slot
  editor. That is our look, not the classic Options'. The owner asked for
  the classic's, so the stone is kept for the plates only, and the panel is
  the classic's dark glass.
- **The loud thing** is one element, the jewelled footer, which is the
  classic's own. Everything else is quiet: grey rules, cream text.
- **The choice rows' steppers** replace combo boxes. A combo box's list is
  a second target on a small screen, and the classic's lists hold two to
  nine entries.
