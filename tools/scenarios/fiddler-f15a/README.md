# F15a localized-string working documents

Layer: L3 editor; toggle: none. Manual work remains available with AI disabled.
Doctor: normal editor setup; no additional codec or service. Contract:
`docs/editor/cliloc_documents.md`. This is an editor-smoke fixture recipe,
following F06a; it introduces no invented runtime scenario verb.

Run `python tools/editor_smoke/run.py --reload`. `EditorSmokeCliloc` creates a
plain synthetic file with a six-byte nonzero header, unknown byte flags, UTF-8
text and duplicate IDs. It verifies:

- Last duplicate wins, stable IDs, exact header preservation and immutable header access.
- Edit/add/remove; removed IDs remain reviewable; undo/redo/revert restore flags and text.
- Language-isolated save/reopen and unchanged source bytes; undo after save becomes dirty.
- Invalid UTF-8, text limits, duplicate document IDs, changed flags, malformed BWT and changed sources are refused.
- Invalid pending text survives selecting another record and blocks that language's save.
- Raw draft recovery is source-bound; malformed recovery is atomic, retained once and explicitly discardable.
- Native Add selects the new ID. Reopen Cancel preserves work; confirmed Reopen discards unsaved work and recovery.
- Directory aliases into a synthetic install are refused. PNG export through a hardlink leaves its backing file unchanged.
- Installed English and Japanese BWT files load while their hashes and timestamps stay unchanged.

For authorized visual review run
`python tools/editor_smoke/run.py --windowed --assets-workbench --out FOLDER`.
It captures the real English working-text inspector and Japanese source view
after the existing workbench/replacement captures. It performs no native cliloc
export. Inspect wrapping, accessible controls and the persistent footer.

Manual walkthrough: Assets → Cliloc → choose a language → search 500207 → edit
Working text → switch to another ID and back → Undo/Redo → Save copy → Reopen.
Add a spare ID and remove it; Undo must restore it. Source text and the selected
language's flags remain unchanged. A working patch is not a runtime translation
or a native binary cliloc save.
