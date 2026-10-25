# F06b — verified native item metadata

Layer: L3 editor and Tools. Toggle: editor-only, manual with AI disabled.
Doctor: `launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`).
Docs: `docs/editor/item_metadata_native_staging.md`.

1. Select occupied static art against modern client data. Edit its name, height
   and a high/unknown flag bit. Invalid pending fields must refuse native export.
2. Export verified item metadata to a disposable external stage. Prepare must
   leave the destination unchanged. Inspect current and exported values at
   1366×900, 1024×768 and 1920×1080 logical editor widths.
3. Cancel Apply and confirm the stage is unchanged. Reopen the confirmation and
   Apply. The pinned ClassicUO reader must see the intended fields and exact raw
   record; untouched fields, records, land bytes and headers remain identical.
4. Repeat with prior staged weight/layer/count/animation/hue/light edits and an
   unrelated file. Those changes must survive. Recover the entire prior stage;
   for a first export, recover the prior absence.
5. Change source, saved project copy, pending draft, edits, baseline, candidate,
   manifest or prior stage after review: native Apply must refuse. Stale recovery
   is retained and blocks export. Cancel Reopen to retain it; confirm Reopen to
   discard only the selected item's draft/recovery. Other drafts remain.
6. Close during preparation: the owned process settles and no stage is published.
   A destination supplying the editor's current input is refused. Old layouts
   and winning `verdata` item-group patches are refused explicitly.

Synthetic fixtures: `tools/fiddler_parity/proof_metadata.py`,
`tools/uodata_write/test_metadata_records.py`. Editor coverage is in
`EditorSmokeItemMetadata.cs` and `EditorSmokeAssetWorkbench.cs`. Run the normal
headless reload and the `--windowed --assets-workbench` native proof. No shard
is required; source client data is never written.
