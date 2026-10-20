# Cliloc CSV exchange

F15c/d is manual L3 editor authoring under `#if TOOLS`. Assets → Cliloc has
icon actions for CSV import and export, with accessible names and tooltips.
Choose the exact installed language first; Speech mode has no CSV authoring.
No binary cliloc writer or runtime localization change is introduced.

## Formats

Both formats use UTF-8 without BOM on export, semicolon-separated
`Number;Text;Flag` columns, decimal non-negative IDs and CRLF records. Import
accepts a UTF-8 BOM and optional header. The Fiddler dialect also detects BOM-marked
UTF-16 LE/BE and UTF-32 LE/BE, matching its `StreamReader`; files without a BOM
use strict UTF-8. Quoted UTF-8 stays explicitly UTF-8. Malformed encoded text is
refused rather than decoded with replacement characters. Blank records and comment records
beginning with `#` are ignored. Flags accept `Original` (0), `Custom` (1),
`Modified` (2), `Custom, Modified` (3), or a decimal byte. Unknown byte values
are preserved numerically on export. Files are bounded to 64 MiB and 500,000
records. Invalid UTF-8, NUL, duplicate IDs, malformed records or flags refuse
the whole import with a repair message.

**Fiddler CSV** follows Fiddler's line-based format: no quoting or escaping,
and input text is trimmed. Export refuses semicolons, line breaks or surrounding
whitespace that Fiddler's importer would lose. Select **Quoted UTF-8 CSV** for
those strings. That dialect preserves text exactly, quotes fields containing
semicolons, quotes or line breaks, and doubles embedded quotes. It is a separate
spreadsheet interchange dialect; Fiddler's importer does not understand it.
Choose the matching dialect explicitly when importing; there is no heuristic
format detection.

The reference is pinned Fiddler 4.24 commit
`610c930833dac8d158f08a0af36da43dd90d08b4`,
`UoFiddler.Controls/UserControls/ClilocControl.cs:OnClickExportCSV` and
`OnClickImportCSV`, plus `Ultima/StringEntry.cs:CliLocFlag`. Fiddler exports
enum names, skips malformed rows, and changes flags to Modified/Custom on
import. GUO deliberately rejects malformed input atomically and retains the
working-document source flags. Incoming flags are advisory and their mismatch
count is visible in review; they do not authorize native metadata rewriting.

## Import review and undo

CSV contains no language identity. The chooser names the target language and
asks the author to verify it. Import commits valid pending text drafts for that
language as one transaction; invalid drafts or failed recovery block exchange.
It then captures the CSV bytes and reviews changes against the working copy.
The review shows total, added, changed and unchanged counts, current/incoming
text, and placeholder/reference warnings. The first 250 changed rows are shown;
the import includes all validated rows, within the document's existing limits.

Listed text is merged by ID. Omitted IDs are unchanged. Blank text is an empty
string, never a deletion. Restoring a removed source ID retains its original
flag; new IDs use flag zero. Review validates the complete candidate patch
before mutation: at most 10,000 changes and 2 MiB of authored text, each edit
at most 32,767 UTF-8 bytes without NUL. An unchanged longer source string can
roundtrip without becoming an authored edit.

**Import into working copy** applies the whole batch in one undo step. Save copy
persists the existing language JSON. Undo after save marks it dirty again. A
source-file change, language reopen/switch, intervening edit or edit-then-undo
invalidates the review. New invalid drafts also block confirmation. Cancel and
validation failure never apply a subset of the CSV rows.

## Export scope and destination

Export chooses the entire active working language, all current browser results,
or the current page's selected rows. Browser results span every matching page;
an ID jump retains the browser's normal full-language results. Removed records
are omitted. Unsaved valid text is included; invalid/recovery-failed documents
cannot silently fall back to source text.

The native save picker confirms an existing filename. Export writes a temporary
sibling and atomically replaces the selected output entry, checking both the
configured and document installation boundaries. Directory aliases are resolved;
atomic replacement avoids truncating an existing hardlink's backing file.
The source cliloc hash is rechecked before publication. No output is written
inside the client installation and no native language file is emitted.

## Verification

`python tools/editor_smoke/run.py --reload` checks exact Fiddler column/enum/UTF-8
bytes, all five BOM variants against .NET `StreamReader`, quoted multiline roundtrips, malformed inputs, atomic budget refusals,
single-step undo/redo, source and revision guards, invalid/recovered drafts,
review confirmation, working export scope and installed ENU/JPN whole-language
quoted CSV roundtrips. The authorized windowed workbench proof captures import
review and the resulting inline working document. The source installation stays
read-only. [Scenario recipe](../../tools/scenarios/fiddler-f15cd/README.md).
