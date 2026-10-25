# Reviewed Fiddler UOX3 export

The explicit `uox3-fiddler` profile exports HOUSE ITEM blocks matching the
hash-pinned Fiddler exporter. Legacy GUO `uox3` remains available for reading
and its earlier indented output. Detection does not select the new profile.
This is L3 manual editor/Tools work, available with AI disabled.

Exports preserve IDs, signed int16 coordinates, order and duplicate entries.
IDs use uppercase four-digit hexadecimal; coordinates are decimal. Blocks
have no indentation, Windows CRLF and one trailing blank line. Numeric ASCII
matches Fiddler's Windows-1252 bytes. The bounded contract is 1–65,535 entries
and 4 MiB; invalid scalar inputs are refused before output.

UOX3 omits hue, visibility/flags and level. The review shows current records
and requires explicit consent when hue or flags would change. The working
document keeps those fields. Nonzero CSV Unk1 is refused; export it with
Fiddler CSV instead. Shared snapshot, destination, cancellation and atomic
write guards apply. Import menus/F3 omit this export-only profile; programmatic
import refuses it with guidance to the legacy UOX3 reader.

The independent probe compiles the unchanged `ExportToUox3File` method from
the same pinned source as TXT/UOA/CSV. It compares output bytes for edge values,
duplicates and deterministic records. Fiddler provides no UOX3 importer in this
source, so this does not certify bidirectional interchange, UOX3 server behavior
or spatial rendering. No generated source is executed by the editor.

Doctor: `launchers/dev/fiddler_parity_doctor.bat` / `.sh`.
Scenario: [F24a UOX3](../../tools/scenarios/fiddler-f24a-uox3/README.md).

Verified in `20261021_guo-worker-fiddler-parity_multi-uox3`: 516 export records
match the pinned exporter byte-for-byte; TXT/UOA/CSV reference regressions,
32 text unit regressions and the full multi suite pass. Clean Windows review
passes 10 UOX3 checks at three widths; the smallest capture was inspected.
Full smoke and assembly rebuild/reload pass (exit 0), with completed native
151-component staging/read-back and unchanged installation in both reload
passes. Existing nonblocking art checks, shutdown warnings and harness skips
remain recorded in the evidence; these results do not certify a UOX3 server.
