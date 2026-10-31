# Item metadata working documents (F06a)

In **Assets → Art → Statics**, select an item. Its name, height and named flags
are editable in the docked **Properties** inspector. **Advanced flags** exposes
the complete 64-bit hexadecimal value; named checkboxes preserve every other
bit. The name has a live UTF-8 byte counter and invalid fields disable saving.

Leaving a valid field commits one undoable document edit. Undo, Redo and the
undoable Revert action operate on the selected item's working copy. **Save copy**
(Ctrl+S while a metadata field has focus) persists only
`<world-project>/metadata/items/0xNNNN.json`. There is no second Apply step.
Text fields keep their native text undo; the document also has dedicated history
buttons. Switching selection retains each item's valid or invalid draft.

Unsaved drafts are backed up in Godot's editor-local project metadata on
workspace teardown/reload. They restore when the same project/item is selected
and the source hash matches; recovery is still unsaved and never auto-exports.
Source mismatches refuse normal project-document save. Saved project documents,
unsaved drafts and project image replacements appear in the **Project changes**
menu. See [Assets workbench](assets_workbench.md) for navigation and export review.

This is editor-only L3 authoring with no AI dependency or feature toggle. The
loaded client metadata and native tiledata file stay unchanged when saving a
project document. F06b adds [verified native staging](item_metadata_native_staging.md)
with a separate current/export review, explicit Apply and recovery. Land records,
bulk edits and old-layout tiledata remain separate stories. Existing
art previews continue to use the installed/overlaid asset metadata.

The document keeps the original modern 41-byte item record. Previewing changes
patches flags at bytes 0–7, height at byte 20 and the name at bytes 21–40. All
other fields remain byte-identical. An unchanged name retains its original
bytes, including bytes after the first NUL. A changed name is UTF-8, at most 20
bytes, NUL-free and zero-padded. Height is 0–255; flags retain all 64 bits.

JSON schema 1 uses `kind: guo/item-metadata`, `layout: modern-41`, integer `id`,
base64 `original`, original-record `source_sha256`, string `name`, hexadecimal
string `flags` and integer `height`. Files are bounded to 16 KiB on load. A
mismatched schema, layout, ID, source record or source hash is refused. Save
checks that the source record is still unchanged and atomically replaces only
the project JSON; externally changed saved copies are refused until reopened.
Resolved paths must remain inside the project and outside
the client installation, including directory-link aliases.

Headless `EditorSmokeAssets` exercises unknown-byte preservation, 64-bit flags,
undo/redo/revert, multibyte name and height refusals, save/reload, source identity,
install protection, draft retention, invalid-save refusal, named-bit preservation,
editor-local recovery and the shared icon toolbar. F06b adds independent native
read-back, exact untouched-byte preservation and editor prepare/apply/recovery
proofs; see its linked staging contract for the supported boundary.
