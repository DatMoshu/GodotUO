# F24a reviewed Fiddler TXT and UOA text exchange

Layer: L3 manual editor and Tools, under `#if TOOLS`; no AI dependency.
Toggle: no exported-client feature. Doctor: existing
`launchers/dev/fiddler_parity_doctor.bat` (or `.sh`); the optional independent
reference requires an explicit private hash-pinned Fiddler source file.
Contracts: [TXT](../../../docs/editor/multi_fiddler_txt.md) and
[UOA text](../../../docs/editor/multi_fiddler_uoa.md).

1. In the existing Multi Editor, explicitly choose Fiddler TXT or Fiddler UOA
   text. Import duplicate records with ID `1650`, offsets `12 -8 7` and
   `13 -7 -2`, and flags `256` and `-2147483648`. TXT must show hexadecimal
   ID 0x1650; UOA must show decimal ID 1650. Automatic detection remains legacy.
2. Inspect the count, bounds, signed flags and coordinate policy. Cancel leaves
   the document unchanged. Confirm without centering preserves offsets/order.
   Repeat with centering: only X/Y change. UOA review explains normalization of
   its first three headers to version 6, template 1 and item version -1.
3. Save/reopen the project and exercise undo/copy/stamps/one-to-one transforms:
   preserve raw flags. Refuse split/join/reorder transforms and native/legacy
   exports that would silently lose opaque flags.
4. Add hue 21. Direct unreviewed text export must refuse. In reviewed export,
   confirmation is disabled until hue loss is explicitly accepted. Export then
   omits hue while the working document keeps hue 21. Cancel creates no file.
5. Refuse malformed/overflow records, mismatched UOA counts, over-budget input,
   source/install/link destinations and changed source/document/destination
   reviews. Check exact Windows output bytes against the pinned unchanged
   Fiddler reader/exporter in both directions, including signed limits and
   duplicates. Preserve source hashes.
6. Run regular editor smoke and assembly reload; verify the multi stage reaches
   its completed marker, reads back the native 151-component fixture and leaves
   the installation unchanged. Inspect review captures at 1024/1366/1920 widths.

```text
python tools/multi/test_fiddler_text.py
python tools/multi/test_multi.py
python tools/fiddler_parity/proof_multi_text.py --format txt-fiddler --reference <Reference.dll> --out <new-folder>
python tools/fiddler_parity/proof_multi_text.py --format uoa-fiddler --reference <Reference.dll> --out <new-folder>
python tools/editor_smoke/run.py --reload --out <new-folder>
launchers/dev/smoke.bat
```

The focused windowed proof uses the existing editor smoke entry point with
`--guo-editor-smoke <new-folder> --guo-multi-text-proof` after Godot's `--`.
These are scalar text contracts. Spatial rendering, native opaque-flag
translation, UO Architect binary and other external formats remain separate.
