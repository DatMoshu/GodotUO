# ADR-0024: Modern gumps

## Status

Proposed — 2026-09-28.

## Date

2026-09-28

## Last Verified

2026-09-28, against main cf2afb7 plus work/ui-fullheight.

## Decision Makers

The owner, who asked for mobile/responsive versions of GUO's gumps
(#bot-hub, 2026-09-28). Written by GUO-UI; the director assigned the
number.

## Summary

A client gump can have a second presentation, **Modern**: a Godot UI built
on UoTheme (docs/ui/uo_godot_style.md), made for touch. The ported gump is
**Classic**: 1:1 with ClassicUO, and what the desktop always gets by default.

A setting per gump, and a global one, chooses between them. Touch builds
default to Modern for the gumps that have it. Modern reads the same game
state and calls the same `GameActions` and packets as Classic. It is another
view, never another client. Shard gumps (sent by the server) and gumps
without a Modern version stay Classic.

## Engine Compatibility

Godot 4.7.2 mono. Modern gumps use the patterns UoTheme already proved on
the window menu, the companion tabs and the slot editor:

- Godot controls in a `SubViewport`, at a whole-number scale, sampled
  nearest-neighbour;
- shown by a `CanvasLayer` on the main screen, or drawn into the second
  screen's bitmap by `DualScreen.Draw`;
- pointer events pushed in by hand, because GameController marks every event
  handled first.

There is no new engine feature and no GDExtension.

## ADR Dependencies

- **ADR-0006** (GameController as a Godot node): Modern gumps are Godot nodes
  beside it.
- **ADR-0009** (second display) and **ADR-0017** (Android): where they are
  shown and how touch reaches them.
- **ADR-0001** (render presenter seam): unaffected. A Modern gump never enters
  the batcher.

## Context

### Problem Statement

ClassicUO's gumps are laid out in fixed client pixels for a mouse. On the
handhelds (the AYN Thor, the Odin 2 Mini) the touch layer's screen scale
leaves their text about 1 mm tall. The largest ones do not fit once they
are scaled to a usable size (docs/ui/tall_gumps.md).

C11 fitted Options to the whole screen: it works, but it is still a mouse
layout, magnified.

### Current State

GUO already has Godot UIs over the client:

- the command bar;
- the window menu;
- the companion tabs (a Journal and a Character view of the same state as
  the classic journal and status gumps);
- the slot editor.

They share UoTheme. Options on touch is fitted and drawn over the bar
(C11). The desktop is 1:1 with ClassicUO, checked by the presentation-parity
probe.

### Constraints

- **The desktop is 1:1.** Classic is the default on the desktop and nothing
  in a Classic gump changes because a Modern one exists.
- **Porting rule 2.** Ported gumps are not rewritten. A Modern gump is new
  code under `src/Input/Touch/Modern/`, and the ported files change only at
  marked hooks.
- **The server contract.** A Modern gump sends exactly what Classic sends,
  through the same `GameActions` calls, and never extra requests.
- **Pixel art** (rule 7). Modern gumps are UoTheme art, nearest-neighbour.

### Requirements

- Finger-sized, fitting the Odin and the Thor's top screen, scrolling where
  content is long, and reflowing to the screen.
- A choice per gump and globally, remembered in the profile.
- A way back to Classic from inside a Modern gump, for anything it does not
  cover.

## Decision

### Architecture

- **`ModernGumps`** (`src/Input/Touch/Modern/ModernGumps.cs`) is a registry:
  a classic gump type maps to a factory for its Modern view.
- **Where gumps open.** One marked hook in `UIManager.Add`, the one place
  every gump passes through, asks the registry first. Classic is the answer
  when any of these hold:
  - it is a desktop without the touch layer;
  - the gump has no Modern version;
  - the profile chooses Classic for it;
  - the gump is from the server.
  
  Otherwise the Modern view opens instead, and the classic instance is not
  added.
- **`ModernGump`** is a base class: a `Node` owning a SubViewport card in
  UoTheme. It has one lifecycle: open, refresh from state, and close. It is
  modal where the classic gump is, and it is draggable only where that
  helps.
- **State and actions.** A Modern gump reads `World`, `Profile` and the
  managers, and it acts through `GameActions` and the same packet calls the
  classic gump makes. Where the classic gump holds logic that is not in
  `GameActions` (Options' Apply writes the profile field by field), the
  Modern gump writes those same profile fields. It covers what it shows, and
  offers **Classic view** for the rest. That opens the ported gump, fitted
  as in C11.
- **The choice.**
  - `Profile.ModernGumps`: Off (all Classic), On (Modern where there is
    one), or Per gump.
  - `Profile.ModernGumpChoices`: a set of classic type names chosen Classic
    while the choice is On.
  - Touch builds start with On (a PlatformDefaults entry); the desktop
    starts with Off.
  - Shown in Options' Touch section.

### Key Interfaces

```csharp
internal static class ModernGumps
{
    public static void Register<TClassic>(Func<World, ModernGump> open) where TClassic : Gump;
    public static bool TryOpenInstead(Gump classic);   // the UIManager.Add hook
    public static bool Chosen(Type classic);            // profile + touch + registered
}

internal abstract partial class ModernGump : Node
{
    public abstract string Title { get; }
    protected abstract void Build(Control root);        // UoTheme controls
    protected virtual void Refresh();                   // from game state, a few times a second
    public void OpenClassic();                          // the way back
}
```

### Implementation Guidelines

- **Order.** Build Modern gumps in docs/ui/gump_index.md's priority order:
  Options first.
- **Design.** Run the frontend-design skill on each one, and follow
  uo_godot_style.md.
- **Proof.** Each gets a touch-probe check and Thor/Odin photos.
- **Hooks.** Every ported file touched gets a `PORT DEVIATION (GUO)` comment
  at the hook, and nothing else.

## Alternatives Considered

### Alternative 1: Scale the classic gump (C11's fit)

The ported gump drawn at 2x and fitted to the screen.

- **Pros:** zero new UI; every setting works.
- **Cons:** still a mouse layout. Its rows are dense, its comboboxes open
  small lists, and the wide gumps (the macro editor at 1095 px) cannot reach
  a usable size.
- **Kept** as the fallback: Classic plus fit, and Modern's Classic view.

### Alternative 2: Reflow the ported gumps in place

Change the ported gumps' layout under the touch layer.

- **Rejected.** Rule 2: every edit would have to be reconciled by hand on
  every upstream merge, across thousands of lines.

### Alternative 3: Modern gumps for shard gumps

- **Rejected.** A server gump's layout is data the shard sends and can be
  anything. It stays Classic, fitted where tall (tall_gumps.md: "Classic
  plus fit").

## Consequences

### Positive

- Touch players get gumps made for a thumb, in the client's own look.
- The desktop and the ported files stay as they are.

### Negative

- Two views of a gump to keep in step: a new game feature in a classic gump
  needs its Modern view updated.
- A Modern Options covers the settings it shows. The rest stay behind
  Classic view until they are added.

### Neutral

- Server behaviour is identical: the same calls and packets.

## Risks

- **Divergence.** Per gump, the Modern view could drift from what Classic
  does. Mitigated because Modern calls the same `GameActions` and writes the
  same profile fields, and each has a probe that compares a change made in
  Modern with the profile field Classic reads.
- **Plugins** (Razor and friends) that look for a classic gump by type will
  not find it while its Modern view is open. For such gumps the choice can
  be Classic per gump. The plugin probe runs with Modern on to catch it.

## Performance Implications

A Modern gump costs its SubViewport while open, like the window menu, which
is well under a millisecond on the Thor. It redraws only when its state
changes, as the command bar does.

## Migration Plan

Nothing to migrate: new profile fields with defaults. A touch profile gets
Modern for the gumps that have it from its first run with this change.

## Validation Criteria

1. **Desktop:** presentation parity 5/5, and the desktop opens every gump as
   Classic.
2. **Touch:** each Modern gump's probe check passes, and its Thor and Odin
   photos show it fitting and finger-sized.
3. **Server contract:** for Options, a setting changed in Modern lands in
   the same profile field the classic Apply writes (probe).

## GDD Requirements Addressed

The owner's request (2026-09-28): "a mobile/responsive version of each
[gump]. Starting with options. Use godot uis for each gump. There
classic/modern gumps. Options gump should try to match the style of the
classic, but be mobile friendly."

## Related

- docs/ui/gump_index.md: every gump, its mobile risk and its plan.
- docs/ui/tall_gumps.md: the measurements.
- docs/ui/uo_godot_style.md: the look.
- ADR-0017: Android and the touch layer.
