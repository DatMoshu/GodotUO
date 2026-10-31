# F06a item metadata document

Layer: L3 editor; toggle: none; doctor: none beyond the normal editor setup.
Contract: `docs/editor/item_metadata.md`, `docs/data_formats.md` section 11.

Use the existing `EditorSmokeAssets` stage through
`python tools/editor_smoke/run.py --reload`. No new runtime scenario verb is
introduced. The synthetic fixture specification is:

- Create a modern tiledata item record with nonzero unknown fields, all name
  padding retained, and a separately specified flags/name/height state.
- Change flags/height and prove untouched bytes and an unchanged name remain
  identical. Exercise the high flag bit and the full unsigned 64-bit mask.
- Undo, redo and revert; verify the exact original record returns.
- Reject a 22-byte UTF-8 name and height 256.
- Save/reopen the project JSON with the original source record unchanged.
- Change the source record; refuse Save. Refuse a project path in the install.
- Construct and tear down the metadata dialog through headless editor smoke.

Manual walkthrough: Art → static → Edit item metadata → Apply to document →
Undo → Redo → Save → Close → reopen. Verify the saved fields return. Close a
dirty document and exercise both Cancel and Discard. The native file and loaded
runtime metadata must remain unchanged throughout.
