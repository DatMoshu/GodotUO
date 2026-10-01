# Parity and Drift

Two things are measured, and neither is estimated. **Parity** is whether GUO
draws and behaves as ClassicUO does in the same place. **Drift** is how far
the ported files have moved from upstream's, and whether upstream has moved
under them.

## The audit

```bat
launchers\pipeline\03_port_audit.bat      REM writes docs\port_status.md
```

`tools\port_audit` classifies every upstream file by how tightly it binds to
FNA (`verbatim`, `shim`, `rewrite`) from its imports, and matches filenames
in `godot\GUO\src`. It says which files exist. It does not say they work:
**"Ported" is not "working"** (AGENTS.md rule 6). CI fails if the committed
`docs\port_status.md` differs from a fresh run. Files that will never be
ported are listed with their reason in `docs\port_waivers.toml`; a waiver
needs an ADR, a `PORT GAP` comment or a AGENTS.md rule behind it and does not
improve the number.

## The PORT DEVIATION rule

Ported files are copied faithfully: no reformatting, renaming, modernising or
opportunistic bug fixing, because every gratuitous edit has to be reconciled
by hand on every upstream merge. Where a change is unavoidable, it is marked
in the source:

```csharp
// PORT DEVIATION (GUO): <why>
```

The marker is what a reviewer greps for, what an upstream merge stops at, and
what a drift tool counts. A hook into ported code that is a no-op off the
feature (the touch layer, the second display, the canvas background) still
gets the marker. Bugs found in upstream code are noted, not fixed mid-port;
fixes are proposed separately (rule 3, parity before improvement).

A drift-measuring tool that counts unmarked deviations against upstream
(`tools\port_drift --strict`, cited in ADR-0016's validation) exists in the
owner's working checkout; it is **not on this branch**, so this page cannot
describe its options. Until it lands, the review is
`git diff` against `sources\ClassicUO` plus the marker grep.

## Upstream drift

```bat
launchers\dev\sync_upstream.bat           REM commits since the pin, flagging ported files
launchers\dev\sync_upstream.bat --pin     REM record the current head as reviewed
```

`docs\upstream\UPSTREAM_PIN.json` records the ClassicUO commit reviewed up
to. The tool lists what landed upstream since, and flags commits touching
files already ported, the ones that rot silently. Assess, port across, then
re-pin.

## Parity by eye: ab_compare

Every visual fault found so far was found by eye, and arguing about one is
slow while nobody has the original in front of them. `ab_compare` stands both
clients in the same spot on the same shard with the same profile (the same
gumps open, the same render options, the same game-window size) and takes a
picture with each.

```bat
launchers\dev\ab_compare.bat [--only guo|cuo|both|compose] [--place P]... [--character C] [--build] [--login-wait S] [--settle S] [--no-open] [--allow-foreground]
```

Output: `build\screenshots\ab\<place>\guo.png`, `cuo.png` and `ab.png` (the
two stacked, labelled).

- GUO is driven with `--play`, `--shard-command "[go X Y"` and
  `--screenshot-name`: one process per place, no window handling.
- ClassicUO has none of that. It is driven the way a person would: autologin,
  the place typed into the chat line, the window photographed off the screen.
  **That pass brings a window to the front and types into it with the
  desktop's keyboard, so it is off by default.** `--allow-foreground` is the
  gate; an agent must not take the keyboard from whoever is working. The
  ClassicUO-side counterpart is commit "Refuse to type unless ClassicUO has
  the foreground".
- CUO borrows the profile GUO saved for the same character, so a comparison
  is not confounded by one client having roofs off.
- `--build` builds the reference ClassicUO first (`build\cuo-bin`).

The recorded sweeps: `docs\parity_2026-09-23.md` (eight places, five
matching, the rest listed with causes) and `docs\parity_2026-09-26_night.md`
(the overnight fixes and the ADR-0007 measurements). Findings there are
numbered (F1 and so on) and each says whether it is fixed and how it was
verified.

## The other parity tools

| Tool | Does |
|---|---|
| `launchers\dev\sweep.bat` | Photographs the same five places every run (a town by day, a forest with buildings, a coast, a dungeon mouth, a town at ground level), so two sweeps can be compared. Needs the shard. |
| `launchers\dev\side_by_side.bat [--place P \| --at X Y [Z]] [--monitor M] [--dump LABEL]` | Both clients live on one monitor in one place; `--dump` makes both write a render dump. |
| `launchers\dev\render_diff.bat LABEL` | Compares a ClassicUO render dump with a GUO one taken in the same place; `--list` shows the dumps there are. |
| `tools\render_dump` | The dump format both clients write on `renderdump LABEL`. |
| `guoasset` (MCP, `tools\guoasset`) | Renders any art, gump or multi from the client data with the reference reader, so an agent can compare a frame against the source art. |
| `tools\world_parity` | The editor's world view against the client's, see [Editor](Editor.md). |
| `--zoom-probe` and the renderer probes | The numbers in the ADRs: frame cost per zoom, batcher checks, blend states, atlas pages. See [Scripted Runs and Probes](Scripted-Runs-and-Probes.md). |

## What "matches" means

A sweep row says **matches** when the stacked frames differ only in moving
things: NPCs, animals, weather text, hover tooltips. Anything else is a
finding with a number, a cause and, once fixed, the tool run that showed it
gone. That is the standard a parity claim in a commit message is held to.
