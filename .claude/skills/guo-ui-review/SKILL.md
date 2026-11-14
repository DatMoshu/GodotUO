---
name: guo-ui-review
description: Review GUO editor tabs, modes, menus and player workflows for complete tasks, clear navigation, recoverable errors, accessibility and lifecycle safety. Includes Pixelorama and the proposed UO Anims workspace.
---

# GUO UI and workflow review

Use [the review contract](../../../docs/review/contract.md) and the relevant
workspace rows in [the section map](../../../docs/review/sections.md).
For Pixelorama or Sprite/Fit Lab, also read
[the integration plan](../../../docs/review/authoring-integrations.md).

Inventory actual registration, labels, defaults and shortcuts from code. Distinguish
top-level workspaces, asset categories, bottom docks, world tools and render modes.
Do not count a hidden tab's backing process or timer as disabled without checking it.

Choose a concrete user task and follow discover -> open -> select/import -> edit ->
preview -> save -> close -> reopen -> stage/consume. Check each step for empty data,
missing dependencies, interrupted work, invalid input, cancellation and retry.
Inspect document ownership, dirty state, undo/redo boundaries and changes of selection.
Opening the wrong asset or losing unsaved work is a correctness defect, not polish.

Review each Assets category independently: Art, Gumps, Anims, Hues, Textures,
Multis, Maps, Cliloc, Sounds, Bulk, Art set and Parity. Re-enumerate registrations
when they change. Review Admin permissions and each World tool/mode separately.

Evaluate keyboard traversal, shortcut conflicts, text-entry focus, pointer capture,
scaling, narrow layouts, long translations, tooltips and readable status. Use the
applicable input devices for each target; player controller parity is separate from
editor keyboard usability. Separate manual authoring from optional AI surfaces.

Follow hot reload and plugin disable/re-enable: current document/selection, active
workspace, signal connections, file handles, browser/native child windows, jobs and
owned-process cleanup. A tab rename must preserve stable document identity and saved
layout migration; it does not prove a workflow is integrated.

Use existing editor smoke queries and reload probes for structural evidence. For
visual/input claims, inspect actual screenshots and exercise the interaction in a
permitted desktop session. Record viewport size and scale. If unavailable, report
source-only findings and the missing interaction proof; never call a headless pass
an artist acceptance test.

Propose navigation around user tasks. Keep unfinished features out of normal main
menus and tabs; an “Extra Tabs” menu is discoverability, not a readiness gate.
