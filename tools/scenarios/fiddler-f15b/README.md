# F15b — verified native Cliloc staging

Layer: L3 editor and Tools. Toggle: manual editor authoring, AI independent.
Doctor: existing `fiddler_parity_doctor.bat --build` / `.sh` launcher pair.
Docs: `docs/editor/cliloc_native_staging.md`.

1. Open installed English/Japanese and an arbitrary-filename Custom 1 source.
   Edit Unicode text, add a new ID and remove an existing ID in the working copy.
2. Prepare an external native stage. Inspect every reviewed row and full text,
   counts, output filename and BWT-to-plain encoding at 1366, 1024 and 1920 widths.
   Preparation alone must leave the stage unchanged.
3. Cancel Apply, then reopen its confirmation and Apply. The pinned exact-file
   Cliloc reader must see the intended effective strings. Untouched raw records,
   flags, order, duplicates and the header remain unchanged. Removing an ID with
   duplicates removes all its occurrences; empty text is not deletion.
4. Identity-export compressed input and require exact compressed-byte identity.
   Repeat with an existing bound stage containing unrelated record/file edits;
   preserve those edits and recover the exact prior complete stage.
5. Change source, saved copy, pending text, selected source, edits, baseline,
   review, candidate, manifest, target-file presence or prior stage after review.
   Refuse stale Apply without changing the stage. Invalid/failed recovery must
   retain its existing reopen/discard behavior and block export.
6. Refuse malformed native rows, foreign source bindings, mismatched installed
   target files, traversal/aliased outputs and unregistered filename collisions.
   Close during preparation: the owned process settles without stage publication.

Fixtures: `tools/fiddler_parity/proof_cliloc.py` and
`tools/uodata_write/test_cliloc_records.py`. Run editor smoke `--reload`, then
the authorized `--windowed --assets-workbench` proof. No source writes, shard
deployment, runtime language activation or font-coverage claim is made.
