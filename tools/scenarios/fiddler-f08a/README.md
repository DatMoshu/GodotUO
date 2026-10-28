# F08a — hue palette working document

Layer: L3 editor, `#if TOOLS`; manual, no AI toggle or extra dependency doctor.

Run `python tools/editor_smoke/run.py --reload --out <external-folder>`.
The asset report contains `hue_document_*` checks before and after reload.
Use an external `UO_BUILD_DIR` for generated fixtures. Run the authorized native
proof with `--windowed --assets-workbench --no-build --out <external-folder>`.
Keep evidence in the shared GUO run folder.

1. Open Assets → Hues → `0x0021`. Select several of the 32 swatches. Edit a raw
   color to `0x83E0`, set a working name, and verify full/partial art previews.
2. Enter invalid hex, switch away and back, and reload the addon. The raw input
   must survive; Save must remain disabled. Undo discards that invalid draft.
3. Pick red with the RGB picker. A prior high bit must survive (`0xFC00`). Undo
   restores the previous raw value. Import a 32-pixel PNG: one undo restores all
   original colors. Import must not write project JSON automatically.
4. Save copy, reopen, search the authored name, then undo. Undo after save must
   mark the working document dirty. Revert must itself be undoable.
5. Externally alter saved JSON: Save must refuse overwrite. Reopen asks before
   discarding unsaved state; cancel preserves it. Foreign source binding, wrong
   hue ID and malformed palettes must be refused.
6. At 1024×768, 1366×900 and 1920×1080 verify swatches stay within the inspector
   and the save footer remains reachable. Hover actions to inspect tooltips.

Parity boundary: native `hues.mul` and optional `verdata.mul` hashes remain
unchanged. Preview uses the existing CPU loader path, not a GPU pixel-parity
claim. Fiddler gradient/text exchange and native staging are separate slices.
