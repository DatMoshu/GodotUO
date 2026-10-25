# F14e sound CSV and batch WAV export

Layer: L3 editor. Toggle/AI: none. Doctor: normal GUO editor setup; no new codec
or service. Contract: `docs/editor/sound_list_export.md`. This recipe invokes
editor smoke, not a new runtime scenario verb.

Run `python tools/editor_smoke/run.py --reload`. `EditorSmokeSoundExport` uses
Python's CSV and WAV readers to check exact columns, names, duration, encoding
and PCM. Synthetic names include semicolons, quotes, newlines, international
text, Windows device names, invalid filename characters and case collisions.
It checks native/POL CSV IDs, German decimal commas in CP1252, UTF-8 refusal
recovery, no output on cancellation, retained existing CSV, atomic WAV folders,
existing-folder refusal and name/source/PCM changes after review. Real panel
checks include working PCM, name/duration sort, numeric jumps after sorting,
browser/selected scopes, native destination pickers, explicit export, music
exclusion and unchanged installed sound-file hashes/stamps.

Manual: select multiple effects → export icon → Selected → WAV batch. Toggle
ID prefixes and inspect every proposed filename. Choose a parent directory;
the new folder must not appear until Export completes. Cancel during a large
scope: there must be no final partial output. Export again into an existing
folder: refuse. Switch to CSV UTF-8, choose a filename, confirm Export and read
it with a semicolon CSV reader. Repeat CP1252 with POL IDs and an appropriate
locale; international names outside CP1252 must require UTF-8. Numeric jumps
must work after name or reverse-duration sorting.

Authorized visual proof:
`python tools/editor_smoke/run.py --windowed --assets-workbench --out FOLDER`.
Inspect `sound-batch-export-review` and `sound-list-csv-review`. This visual
fixture reviews without publishing output. Native archive staging, CSV name
import, music conversion, slot insertion and deletion remain separate work.
