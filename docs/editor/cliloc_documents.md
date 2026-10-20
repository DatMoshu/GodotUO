# Localized-string working documents

F15a, L3 editor authoring. The Assets library's Cliloc page edits one installed
language at a time, or either of two [custom source slots](cliloc_custom_sources.md).
It reads that exact `Cliloc.<language>` file, including BWT
compressed files, instead of enumerating the runtime's merged language view.
English fallback and `Clilocs.txt` remain runtime concerns and are not merged into
this file editor. Speech keyword matching remains a separate read-only mode.

Select a language, search by decimal/hex ID or text, and edit Working text in
the shared inspector. Source text is available below it. Add creates a chosen
non-negative ID with flag zero; Remove records a tombstone in the project copy.
Removed IDs remain browsable so Revert and Undo can restore them. The Project
changes filter shows added, edited and removed records. IDs above the old
3,200,000 browse ceiling are supported when present in the file/document.

The inspector preserves existing byte flags and displays their value. It retains
the six-byte file header and a hash of the original compressed/plain file.
Duplicate source IDs follow the runtime's last-record-wins lookup; the source
file remains intact. Text is decoded strictly as UTF-8. An edit must fit 32,767
UTF-8 bytes and contain no NUL. Placeholder/reference changes produce a warning;
they do not silently rewrite IDs or prevent deliberate script changes.

Undo/Redo applies to the selected language document. Text editing also retains
its normal text-editor undo behavior. Invalid pending text survives selection
changes and disables saving that language, including when a different string
is selected. Save copy commits valid pending fields for the selected language
and writes `metadata/cliloc/<language>.json` under the selected world project.
No binary cliloc file is emitted or changed. Source text is not duplicated into
the JSON except where the working patch itself contains an edited value.

Saved documents are tied to the source SHA-256, language, header and compression
state. A changed source refuses save/reopen rather than rebasing silently.
Project output paths resolve directory links and must remain inside the project
and outside the client install. Writes use a temporary sibling and atomic file
replacement. The working patch is bounded to 10,000 changes and 2 MiB of text;
source files and decoded output are bounded to 64 MiB.

Unsaved documents and raw text drafts are recovered from editor-local project
metadata after addon reload. They are distinct from saved project JSON. The
Reopen action confirms discarding the selected language's unsaved document and
its recovery entry before reopening the source plus saved JSON. A recovery
refusal is shown without overwriting the failed recovery record.

## Verification

`python tools/editor_smoke/run.py --reload` exercises synthetic headers, unknown
record flags, Unicode, duplicate IDs, edit/add/delete, undo/redo/revert,
save/reopen, language isolation, placeholder warnings, malformed input and
source-change refusals. It checks invalid drafts across records, draft recovery,
and the real panel/inspector binding. Installed English and Japanese BWT files
are read with source hash and modification time checks before/after.

The same suite checks project-directory link refusal and PNG export through an
external hardlink: export replaces the output entry instead of truncating its
backing file. These aliases use synthetic fixtures, never the actual install.

F15c/d adds [reviewed CSV exchange](cliloc_csv_exchange.md), including atomic
batch undo and separate Fiddler/quoted UTF-8 dialects. F15b native binary staging,
and independent binary read-back remain separate stories. F15e adds custom-file
opening with source/slot/version-isolated project copies. This document
does not claim a runtime localization pipeline or language/font certification.

F16a/b adds [selected tiledata-name synchronization](cliloc_tiledata_sync.md):
preview mapped Add/Update/Remove changes, include valid project metadata names,
and apply a chosen subset as one project-document undo step.
