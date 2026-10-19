# ADR-0040: One Input page for keyboard and mouse, controller and touch

## Status

Proposed — 2026-10-19. The owner decides; nothing is built yet.

## Date

2026-10-19

## Last Verified

2026-10-19, against main 96ce0ff2.

## Decision Makers

The owner. Drafted by guo-worker-input (story INPUT-05) at the director's
request; the director allocated the number.

## Summary

GUO has three input modes (ADR-0025), but their settings are spread over
the Options window: the controller's on Video, touch on General (touch
builds only), the keyboard's on Macros, and none of them shows which mode is
active. This ADR weighs three ways to give them one page. It recommends
**(b): one marked seam in the classic OptionsGump, a button that opens a
GUO-owned Input page, which Modern Options also shows as a page.**

## Engine Compatibility

No engine API is involved; the page is built from the client's existing gump
controls (NiceButton, Checkbox, Combobox, ScrollArea) and, in Modern
Options, from the Modern rows (ADR-0024). Nearest-neighbour sampling is
unchanged.

## ADR Dependencies

- ADR-0025 (the gamepad on by default; the input mode follows the last
  input): the mode line reads `InputMode.Current`.
- ADR-0024 (Modern gumps): Modern Options gets the same page.

## Context

Where the input settings are on main today:

| Mode | Where | What |
|---|---|---|
| Keyboard and mouse | Options > Macros (page 4, upstream) | Each macro's key: upstream has no other key binding |
| Controller | Options > Video > "Controller buttons" (GUO) | A/B/X/Y layout, "Use a controller", "Set controls...", the button legend |
| Touch | Options > General > "Touch" (GUO, touch builds only) | The command bar's slots and words, Modern gumps, vibrate, reduce motion, chevron inset, the four flicks |
| Any | nowhere | which mode is active |

Modern Options (touch only) has a Touch page and no controller page. The pad's
own Options screen (PadScreen) opens "Set controls" directly.

`OptionsGump.cs` is a ported file: 5,557 lines with 81 `PORT DEVIATION
(GUO)` markers already. Upstream changes this file more often than most, so
every line of drift there is reconciled by hand on each upstream merge
(AGENTS.md rule 2). The page column holds thirteen buttons, 30 px apart from
y 10 (the last, Ignore list, ends at y 395), then the rule at y 441 above the
footer: room for exactly one more, at y 400.

Modern Options applies only with the touch layer on (`ModernGumps`); the
desktop always gets the classic window, 1:1.

## Decision

Proposed: **(b)**.

1. **One seam in OptionsGump.** An "Input" tab appended to the page column
   (the fourteenth, at y 400, page 13), marked `PORT DEVIATION (GUO)`. Its
   page is filled by the GUO builder, so OptionsGump gains the tab and one
   call, not the controls. No upstream page moves or is renumbered (Macros
   stays page 4, which MacroControl opens).
2. **The Input page's content is GUO's own** (`src/Input/InputSettings*`),
   one builder for both views: the classic tab's page, and a page in Modern
   Options (whose Touch page it replaces).
3. **On the page, top to bottom:**
   - The mode line: "Using now: keyboard and mouse / controller / touch",
     live, and a sentence that any input switches it (ADR-0025).
   - **Keyboard and mouse:** "Keys are set on each macro" with a button to
     the Macros page. No new key binding is invented; upstream's model stays.
   - **Controller:** the existing section: layout, "Use a controller",
     "Set controls..." (the binding list), the legend.
   - **Touch** (shown on touch builds): the existing section, unchanged.
4. **The two GUO sections then leave the upstream pages.** "Controller
   buttons" leaves Video and "Touch" leaves General. That takes their
   deviations out of `BuildVideo` and `BuildGeneral` (about 100 lines), so
   those pages come back to upstream's own code. This is the step that makes
   (b) reduce drift rather than add to it.
5. No new profile field. The settings and their apply code are the ones that
   ship today; only where they are drawn changes.

```
 Options (classic, one tab added)                   Input page (GUO builder)
 ┌────────────┬──────────────────────────┐         ┌──────────────────────────────┐
 │ General    │                          │         │ Using now: controller        │
 │ Sound      │   (upstream pages, as    │         │ ─────────────────────────────│
 │ Video      │    ClassicUO draws them) │         │ Keyboard and mouse           │
 │ Macros     │                          │         │  Keys are set on each macro  │
 │ ...        │                          │         │  [ Macros... ]               │
 │ Ignore list│                          │         │ Controller                   │
 │ Input      │                          │  ───▶   │  A/B/X/Y  [Automatic     v]  │
 │────────────┼──────────────────────────│         │  [x] Use a controller        │
 └────────────┴──────────────────────────┘         │  [ Set controls... ]  legend │
                                                   │ Touch (touch builds)         │
                                                   │  command bar, flicks, ...    │
                                                   └──────────────────────────────┘
```

## Alternatives Considered

### (a) The Input page in Modern Options only; the classic window untouched

- **For:** zero new drift in OptionsGump; Modern is GUO's own code.
- **Against:** Modern applies only with touch on, so a desktop player with a
  pad or a keyboard, the largest group, never sees the page, and the
  controller section stays on Video. Fixing that means opening Modern
  Options on the desktop, which is a product call well beyond this story.
- Fails the story's job for most players.

### (b) One marked seam: an appended tab whose page GUO builds (recommended)

- **For:** a few lines of new drift (one tab, one call), and moving the two
  existing GUO sections out takes about 100 lines of drift away, so the net
  drift goes down. Works on every platform, in both views, and looks native.
  Rule 2 favours it.
- **Against:** it uses the column's last free slot; a later GUO page would
  need another answer (a "More" tab). The moved settings' apply must move
  with them (Apply and OK still apply them; they read the same profile
  fields). A variant puts an "Input..." button under the rule that opens the
  page as its own window, which leaves the column free but costs a click.

### (c) Restructure OptionsGump around the three modes

- Regroup the upstream pages: the mouse settings out of General, Macros
  renamed to Keyboard, the controller and touch sections beside them, the
  controls built inside OptionsGump.
- **For:** the tidiest window, one place for everything.
- **Against:** it rewrites upstream's own pages, and any reorder renumbers
  them (MacroControl opens Macros as page 4). Each upstream
  merge of a file it changes often gets harder. Rule 2 argues against it.

## Consequences

- Classic Options gains one button and loses two GUO sections; Video and
  General draw as upstream does.
- Modern Options' Touch page becomes the Input page, with a controller part.
- The PadScreen's Options entry can open the Input page instead of the
  binding list straight away; this is optional.
- Docs: Controller.md, Mobile-UI.md and the Options sections of the wiki
  point to the Input page.

## Risks

- Moving the sections must not change what Apply does: the probe below
  checks each setting round-trips through Apply before and after.
- A player who learnt "Controller buttons is on Video" looks there; the
  Video page could keep a one-line pointer for a release, at the cost of one
  more marker.

## Performance Implications

None; one gump, built when opened.

## Migration Plan

One PR after the owner's decision: the builder and both views, the seam,
the two sections moved, probes, docs. About 400 lines; within the PR
ceiling.

## Validation Criteria

- A shard-free probe opens Options, presses Input, and finds the mode line
  naming the mode of the last injected input (a key, a pad button, a touch),
  each section, and each setting applied through OK.
- `BuildVideo` and `BuildGeneral` have no GUO markers left for the moved
  sections; the classic window otherwise matches upstream (side-by-side).
- Scenario `client.input.settings`, and a smoke line.

## GDD Requirements Addressed

Sprint story INPUT-05: "one Options section that shows the active mode
(ADR-0025 input modes) and rebinds for all three."

## Related

- `docs/input_audit_2026-10-18.md` (INPUT-01)
- `docs/wiki/Controller.md`, `docs/wiki/Mobile-UI.md`
- `docs/ui/modern/options.md`
