# F14b sound naming working document

Layer: L3 editor. Toggle/AI: none. Doctor: normal GUO editor setup. Contract:
`docs/editor/sound_documents.md`. This is an editor-smoke fixture recipe, with
no invented runtime scenario verb.

Run `python tools/editor_smoke/run.py --reload`. `EditorSmokeSoundDocument`
creates isolated project/install fixture directories and signed 16-bit mono PCM.
It checks name editing and UTF-8 limits, immutable sample copies, save/reload,
undo/redo/revert, wrong ID/hash refusal, changed-source refusal, install boundary,
invalid raw drafts, local recovery, stale recovery refusal and explicit discard.
It also saves/searches/reopens one real effect's project name and compares sound
source SHA-256, byte size and mtime before/after. PCM is never imported or changed.

Manual walkthrough: Assets → Sounds → Effects → select an effect → edit name →
switch selection and return → undo/redo → Save copy → Reopen sound → Cancel →
Reopen sound → confirm. Enter 21 copies of `é` (42 UTF-8 bytes): save must refuse
while the draft survives selection changes. Correct or undo it before saving.
Rebuild/reload the editor with an unsaved valid name and confirm draft recovery.
The normal WAV exporter must keep the same sample data after a naming edit.

For authorized native UI review, run
`python tools/editor_smoke/run.py --windowed --assets-workbench --out FOLDER`.
Inspect the sound working-name capture and the persistent save/undo footer.
