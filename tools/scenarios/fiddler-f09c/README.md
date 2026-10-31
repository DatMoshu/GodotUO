# F09c — verified native texture export

Layer: L3 editor and Tools. Toggle: editor-only, manual with AI disabled.
Doctor: `launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`).
Docs: `docs/editor/texture_native_staging.md`.

1. Open Assets → Textures against a configured source and disposable world
   project. Replace an occupied texture with a 64/128-square PNG and save its
   working copy. Source files remain read-only.
2. Choose **Export verified texture stage**, set a new external staged folder
   and Prepare. Confirm no destination was published. Inspect current/candidate
   textures, native dimensions, physical row and the complete affected-ID list.
3. Apply opens a separate confirmation. Cancel it and verify no stage changes.
   Confirm Apply, independently read the requested ID with the exported source
   definitions, and compare raw bytes/pixels and all untouched index/prefix bytes.
4. Recover the previous stage; first-ever exports restore absence. Repeat with
   an existing stage containing unrelated content and registered TexTerr.def.
5. Change source, edits, definitions, prior stage or candidate after preparation:
   Apply must refuse. Close during preparation: its owned child settles and the
   destination remains unchanged. Inspect prepared/applied/recovered dialogs at
   1366×900, 1024×768 and 1920×1080 logical editor widths.

Synthetic fixtures: `tools/fiddler_parity/proof_texmaps.py` and
`tools/uodata_write/test_texmaps.py`. Editor model/reload checks are in
`EditorSmokeTextureDocuments.cs`; `--windowed --assets-workbench` drives the
actual prepare/apply/recovery/cancel workflow. No shard is required.
