# Controller backlog

Ranked work for GUO gamepad support. Acceptance checks are what a probe or a
pad feel-pass must show. Update status as items land; keep
`docs/wiki/Controller.md` honest.

Status: `todo` | `doing` | `done` | `blocked`

## P0 — visible bugs (do first)

| ID | Item | Acceptance | Status |
|---|---|---|---|
| B1 | Item names: strip tiledata `%s%` / plural markers (e.g. "clean bandage%s%" → "clean bandage") | Backpack / paperdoll detail never shows raw `%…%` codes | done |
| B2 | Button glyphs legible on radar + Set controls; no blank discs; no "LB LB" duplicate | A/B/X/Y/LB/RB/LT/RT/L3/R3/Start/Back readable; Set controls shows glyph **or** words once | done |
| B3 | Backpack icons: whole-number upscale for small art; grid clear of centre strap; no right strap stub | Icons integer-scaled nearest; cells 6–7 not under strap; cropped body has no strap remnant | done |
| B4 | Focused-item detail: larger, in empty pocket (not over brass clasp) | Detail readable in pocket band; not overlapping clasp art | done |
| B5 | Paperdoll / Status / Skills: proper UO-art labels; Status without raw coordinates | Each screen has clear labels; Status vitals only (coords belong on World map) | done |
| B6 | Hide 18px chat strip in gamepad mode | No bottom chat input strip while InputMode is Gamepad (speech still reachable later via planned talk flow) | done |

## P0 — hard flows

| ID | Item | Acceptance | Status |
|---|---|---|---|
| T1 | Target-cursor flow: radar for entities; self / last / cancel; ground reticle stub | With a target cursor up, RT+A answers an entity; B cancels; self chord works; ground spell documents remaining gap | todo |
| G1 | Generic gump focus-graph navigation | D-pad moves focus across buttons/checkboxes/text/scroll on an arbitrary server gump; A activates; B closes; one code path, not per-gump screens | doing — PadGumpNav wired; needs live server-gump probe + focus ring polish |

## P1 — solo essentials

| ID | Item | Acceptance | Status |
|---|---|---|---|
| S1 | Bandage self direct or macro-row default | One pad path bandages self without opening the pack | todo |
| S2 | Last object / last spell on macro row | Favorites or Y-row entries fire LastObject / LastSpell | todo |
| S3 | Corpse loot screen actions | From radar-opened corpse, take / dig without mouse | todo |
| S4 | Talk / speech pad path | Compose speech without a physical keyboard (OSK or fallback) | todo |
| S5 | Pet command favorites | all follow me / stop / stay reachable | todo |

## P2 — later (not solo-blocking)

| ID | Item | Acceptance | Status |
|---|---|---|---|
| L1 | Vendor buy/sell pad screen | Buy and sell without mouse | todo |
| L2 | Trade / party invite | Needs second player; pad UI only after client path proven | todo |
| L3 | Guild / quests / housing | Dedicated screens or focus-graph over classic gumps | todo |
| L4 | Role presets (caster / healer / tamer / gatherer) | One-tap load of favorites | todo |

## Blocked

| ID | Item | Reason |
|---|---|---|
| R1 | Radar “release RT to use” after opening a context menu | Probe still sees LastResult `menu …` on the next use-on-release step (pad-wheels14). Target-cursor B cancel is fixed. Needs a focused radar-state reset after Y-menu, not more PadGumpNav churn this pass. |
| R2 | Trackpad-while-RT choice lock | Failed once on pad-wheels14; not investigated (Deck-specific path). |


## Out of scope this pass

- Steam Deck push, Discord, GitHub PR/push
- Multibox / character swapper (reserved jobs only — see Controller.md)
- Depending on Moshu PR #17 scenario runner (ideas only; not on main-local)
