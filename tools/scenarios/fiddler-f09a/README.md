# F09a texture browsing

Layer: L3 editor-only, `#if TOOLS`. No AI gate, client toggle or new dependency.
Use the normal GUO client-data and Godot configuration. Never write the install.

1. Run `python tools/editor_smoke/run.py --reload --out <new-folder>`.
2. Inspect `assets.texture_*` in `report.json` and `before_reload.assets`.
   Both dimensions, when present in the selected library, must pass ID search,
   filtering, opaque image checks and exported PNG read-back. The report names
   a missing dimension rather than inventing a fixture for that library.
3. The synthetic index checks must reject a short file, negative/overflowing
   offsets and unknown record lengths. Offset zero with a complete record is
   valid. A malformed effective entry must never reach the unsafe decoder.
4. Run the authorized native proof:
   `python tools/editor_smoke/run.py --windowed --assets-workbench --out <new-folder>`.
   View `texture-browser-*` captures at all three widths; the inspector and its
   export action must remain usable. This broader proof also exercises disposable
   native hue staging and needs the existing parity doctor build.
5. Manually select **Textures**, search a referring land name, switch dimensions,
   select a result and export PNG outside the source installation. Verify the
   original texture remains unchanged. No native save is offered by F09a.

Docs: `docs/editor/texture_workbench.md`. Run the required full project smoke
before committing. These checks certify browsing/interchange, not native writes.
