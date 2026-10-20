# F15c/d Cliloc CSV exchange

Layer: L3, manual editor-only authoring, always available without AI. No runtime
toggle, dependency service, native binary writer or client scenario verb.

1. Run `python tools/editor_smoke/run.py --reload`. Inspect `assets` entries
   beginning `cliloc_csv_` before and after reload, plus the invalid-draft and
   failed-recovery exchange guards. Every boolean check must be true.
2. Synthetic fixtures cover exact Fiddler UTF-8/CRLF/enum output, legacy trims,
   quoted multiline text, blank strings, unknown flags, malformed/duplicate
   records, atomic patch budgets, review staleness, batch undo/redo and saves.
   Five BOM encodings are compared with .NET StreamReader and used in reviewed
   imports; malformed UTF-16/UTF-32 input is refused without replacement text.
3. Installed ENU/JPN whole-language quoted exports are parsed and compared by
   ID/text/flag; unchanged re-imports must not create changes or history. Verify
   the source SHA-256 and mtime checks remain true.
4. With owner-authorized window access, run
   `python tools/editor_smoke/run.py --windowed --assets-workbench` and inspect
   the Cliloc CSV import-review and working-document screenshots. The imported
   synthetic labels are undone before the proof exits.
5. Run the complete `launchers/dev/smoke.bat` before committing. Evidence stays
   in the shared run folder; client strings/art and generated CSV are not
   committed. This proves editor interchange, not native cliloc binary parity.
