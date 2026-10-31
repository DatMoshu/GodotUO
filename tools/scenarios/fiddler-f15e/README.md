# F15e — Custom Cliloc source documents

Layer: L3 editor authoring. No runtime toggle or external dependency.
Run `python tools/editor_smoke/run.py --reload --out <evidence>/editor-reload`.
With the owner's window authorization, run
`python tools/editor_smoke/run.py --windowed --assets-workbench --out <evidence>/visual`.

The reload fixture creates two equal-byte plain files with arbitrary extensions,
unknown flags, Japanese text and a placeholder; an empty six-byte source; and a
malformed source. Open them through both custom slots and verify independent
saved documents and recovery identities. Edit a raw invalid draft, switch files,
and return: the draft must remain invalid and block CSV/save. Save another
source's valid edit, change source bytes, and explicitly choose it again: the new
version must not inherit the prior patch. Restoring original bytes reopens that
version's prior document. Test CSV import into an empty source and undo it.

Refuse CSV over the active source, another opened source and a symlink alias;
refuse shared project/export outputs over registered sources. Compare original
source bytes after the attempted writes. Read the installed Japanese BWT file
as Custom 2 and compare its raw SHA-256. Missing remembered files must report
failure without installed-language fallback. Speech must disable custom tools.
Restore editor-local paths/drafts in finally so fixtures do not become user prefs.

Windowed proof uses a synthetic quest file, edits its text, then reviews CSV.
Inspect the filename/slot, full IDs, working/source text, save footer and
current/incoming comparison. It cancels the review and discards its fixture
draft. Proprietary installed-language screenshots stay in shared evidence.

This proves working-document authoring and standard-file interchange. Native
Cliloc writing, independent binary read-back and runtime localization are separate.
