# F08f — native hue stage

Layer: L3 editor / Tools. Toggle: editor build (`#if TOOLS`). AI: not required.
Doctor: `launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`).
Docs: `docs/editor/hue_native_staging.md`.

1. Build the oracle, run `proof_hues.py` and the existing static-art proofs.
   Synthetic groups have nonzero headers, high-bit colors and raw name padding;
   edits span IDs 1, 8, 9 and 16. Independently verify target records, bounds and
   colors; preserve all other bytes. Corrupt each category and require refusal.
2. Verify source, input, manifest and stage changes refuse promotion. Inject a
   failed candidate rename and prove exact prior-stage restoration. Apply and
   recover both an existing stage with unrelated files and prior stage absence.
3. Against the configured local library, prepare a disposable hue stage outside
   the install, explicitly apply, independently read it, recover it and verify
   source hashes before/after. Never commit or redistribute the derived file.
4. In Assets → Hues edit a palette. Review native name conversion and before/after
   swatches, prepare, then explicitly apply. Capture prepared/applied states.
   Change the working document or project and require a fresh preparation.
   Close during work, reload the addon, and verify owned-process cleanup.
5. Run editor reload smoke and full project smoke; retain reports and relevant
   windowed proof centrally. Native certification requires an independent reader,
   not GUO decoding its own export.
