# Sound lists and batch WAV export

F14e is L3 editor authoring under `#if TOOLS`, available without AI or additional
codecs. Assets → Sounds → Effects has an icon-only export action with a tooltip.
Multiple selected rows also offer **Export selected sound WAVs** in their context
menu. Music is excluded. The sound browser sorts by ID, name, shortest or longest
working duration; ID jumps still find the correct item after sorting.

The export review captures **All effects**, **Browser results** and **Selected**
scopes when opened. Browser results include every matching page, not just visible
rows; an ID jump retains the normal whole-library browser results. Selection is
the current page's multiselection. Scope counts are explicit. The table shows up
to 250 review rows; export includes the entire chosen scope. Reopen the dialog
to capture a changed browser selection. Names and PCM come from valid working
documents, including unsaved edits. Pending valid names are committed before
review. Invalid documents or names refuse preparation rather than substituting
source data. Unopened effects use active loader names and PCM.

## CSV profiles

Both profiles use `ID;Name;Length`, semicolon separation and CRLF records.
IDs use `0x` plus at least three uppercase hex digits. Length is working duration
in seconds with two decimal places. Native IDs are the default; **POL IDs (+1)**
changes only CSV IDs, never document IDs or WAV filename prefixes.

**UTF-8** uses no BOM and an invariant decimal point, retaining international
names. **Fiddler Windows-1252** uses strict CP1252 and the current locale's decimal
separator. An unrepresentable name refuses export with an instruction to choose
UTF-8. Fields containing semicolons, quotes or newlines use CSV quoting with
doubled quotes. This fixes ambiguous names without losing their text.

The pinned Fiddler 4.24 source (`610c930833dac8d158f08a0af36da43dd90d08b4`,
`Ultima/Sound.cs:SaveSoundListToCsv`) establishes the columns, CP1252, ID offset
and two-decimal locale convention. Its writer does not quote special names;
GUO documents that improvement rather than reproducing malformed CSV. No sound
name CSV import is implied by this exporter.

Choosing a filename does not write it: the review's **Export** confirms the
destination. Native picker overwrite confirmation is retained, and the explicit
export uses temporary-sibling atomic replacement. Cancel/failure before
completion preserves an existing output file.

## WAV batches

Choose a parent folder, then review the proposed new output folder. Existing
folders are refused; batch export never silently replaces a previous export.
By default files are named `0xNNNN name.wav`; the ID prefix can be disabled.
Illegal filename characters, control characters, trailing spaces/dots and
Windows device names are normalized. Missing names use `sound`. Names with
`.wav` in either case retain a single extension. Case-insensitive collisions
block export until ID prefixes are enabled or working names are corrected.

Each WAV contains the exact reviewed mono 16-bit 22,050 Hz PCM. A UTF-8
`SoundList.csv` with native IDs accompanies the batch. Audio bytes are read one
effect at a time, checked against the reviewed PCM hash, name and source binding,
then written into a private temporary sibling folder. At completion the whole
folder is renamed into place. Changed inputs or I/O failure leave no published
partial folder. Cancel is available while the incremental job runs; teardown
also cancels it. Cleanup deletes only the exact temporary files this job wrote,
never recursively deletes a directory, and reports cleanup failure. Unexpected
external files in the temporary directory are retained for attention.

Export progresses in small main-thread batches so the single-threaded asset
loaders remain on their supported thread. Cancellation is observed between
effects, not inside an individual file write. Source-install destinations are
refused. CSV/WAV export does not save working documents, alter native sound
archives, create sound slots or modify runtime audio.

## Verification

`python tools/editor_smoke/run.py --reload` exercises independent Python CSV
and WAV readers, UTF-8 quoting, strict CP1252, locale decimals, POL offsets,
filename normalization/collisions, cancellation and changed-input failures,
atomic batch publication, native pickers, actual working names/PCM, sorting and
ID jumps, all/browser/selected scope wiring, and source-file SHA-256/size/mtime.
The authorized `--windowed --assets-workbench` proof captures WAV and Fiddler
CSV review layouts. [Fixture recipe](../../tools/scenarios/fiddler-f14e/README.md).
