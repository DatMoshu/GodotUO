# Item metadata working documents (F06a)

In the Art inspector, select a static and choose **Edit item metadata…**.
Edit its name, 64-bit flags in hexadecimal and height. **Apply to document**
creates one undoable edit; Undo, Redo and Revert to source operate on the working
copy. **Save project document** also captures the current fields and saves
`<world-project>/metadata/items/0xNNNN.json`. Reopen the item to reload it.
Closing with unsaved changes asks before discarding them.

This is editor-only L3 authoring with no AI dependency or feature toggle. The
loaded client metadata and native tiledata file stay unchanged. Binary staging,
land records, bulk edits and old-layout tiledata are separate stories. Existing
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
the project JSON. Resolved paths must remain inside the project and outside
the client installation, including directory-link aliases.

Headless `EditorSmokeAssets` exercises unknown-byte preservation, 64-bit flags,
undo/redo/revert, multibyte name and height refusals, save/reload, source identity,
install protection and dialog construction/teardown. It does not certify native
tiledata output, which this milestone does not produce. Windowed visual and
interaction review remains separate.
