# F16a/b — Selected tiledata-to-Cliloc synchronization

Layer: L3 editor authoring. No runtime toggle or external dependency.

Run `python tools/editor_smoke/run.py --reload --out <evidence>/editor-reload`.
With the owner's existing window authorization, run
`python tools/editor_smoke/run.py --windowed --assets-workbench --out <evidence>/visual`.

The fixture constructs complete modern/legacy tiledata blocks, ASCII names,
first-NUL padding, a full 20-byte name and a non-ASCII byte. A synthetic Cliloc
contains equal text, a token-bearing update, a whitespace-name removal, an empty
existing string, and an unrelated ID. Preview must produce the exact action set.
Select an update and add, apply once, undo once, and confirm both reverse together
without touching other IDs or flags. Save/reopen must preserve source bytes.

Exercise all six mapping boundaries plus invalid IDs; source/layout/partial-block
refusals; saved metadata names, pending Unicode names, a draft reverting a saved
name, unvisited recovery and corrupt/foreign/duplicate metadata. Any source or
saved/draft change invalidates review. New invalid Cliloc text blocks Apply.
Select a plan exceeding the patch budget and confirm atomic refusal.

In the real owned review, check no initial selection, filters, selection across
pages, all matching rows versus the current page, hidden selection counts,
explicit removals, captured current/proposed text, stale errors and cancel.
The native proof uses a disposable three-row fixture; it selects Add and Update,
leaves Remove excluded, applies, captures inline text, then undoes the whole batch.

This certifies project-only synchronization. It does not certify native binary
output, runtime localization, reverse sync or all variant-specific navigation.
