# F08d/e — hue palette text exchange

Layer: L3 editor, `#if TOOLS`; manual, no AI toggle or extra dependency doctor.

Run `python tools/editor_smoke/run.py --reload --out <external-folder>` and
inspect `assets.hue_text_*` before and after reload. The authorized native proof
uses `--windowed --assets-workbench --no-build --out <external-folder>`.
Use external build paths and retain shared GUO evidence.

1. Open Hues, select an ID, then More → Import palette text. A 35-line file
   contains name, decimal start/end, and 32 decimal colors, each 0–65535.
2. Choose the declared encoding: Fiddler Windows-1252 or UTF-8. Review both
   palettes, full names and bounds. Cancel preserves everything. Confirm edits
   the working document; one undo restores all fields. Save copy persists it.
3. Try a missing/extra line, invalid number, wrong BOM or malformed UTF-8. The
   import must be disabled with an explanation. Working/source changes while
   reviewing must refuse confirmation; the captured file stays immutable.
4. Export both encodings through More. Windows-1252 must refuse an unsupported
   name and preserve an existing destination. UTF-8 must retain Unicode. Native
   install destinations must be refused; output is atomic CRLF text without BOM.
5. Use the Hues toolbar name-list export. Display hue `0x0021` must write index
   `0x20`; names use UTF-8 and retain project names and source spacing. Pending
   drafts or stale project documents must refuse export.

Names are limited to 4,096 UTF-8 bytes; palette files to 64 KiB, name lists to
16 MiB. No native hue writer, staging, insertion or deletion is added.
