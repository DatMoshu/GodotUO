# F09b texture documents

Layer: L3 manual editor, `#if TOOLS`; no client option or AI dependency.
Documentation: `docs/editor/texture_workbench.md`.

1. Run `python tools/editor_smoke/run.py --reload --out <new-folder>` with normal
   configured data and pinned Godot. Inspect `assets.texture_document_*` before
   and after reload: full 16-bit source data, PNG quantization/alpha/zero, 64/128
   replacement, undo/redo/revert, save/reopen and recovery must pass.
2. Stale source bytes, index/remap identity, project file and review tokens must
   refuse changes. Wrong-size/truncated PNGs and recovery errors must fail.
3. In the editor, select **Textures**, select grass 0x0003, choose **Replace
   texture from PNG** and review a synthetic 128-square image. Cancel once;
   the document must stay unchanged. Confirm the next review, undo, redo, then
   **Save copy**. Switch selection and reopen it; the project image remains.
4. Toggle **Source** to compare the original with the working copy. Revert is
   undoable. **Reopen texture** discards unsaved edits only after confirmation.
5. Close/reload with an unsaved replacement and confirm recovery in the same
   project/library. A different saved copy must refuse that recovery.
6. Run `python tools/editor_smoke/run.py --windowed --assets-workbench --out <new-folder>`
   for native comparison/working/saved views at the supported widths. The broader
   proof also exercises native hue staging and needs the parity doctor build.
7. Run full project smoke before commit. No files under the source installation
   may change. Project save is not a native texture export certification.
