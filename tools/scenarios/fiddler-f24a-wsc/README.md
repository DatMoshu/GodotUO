# F24a reviewed WSC

Layer: L3 manual editor and Tools, `#if TOOLS`; AI is optional and unused.
Toggle: no exported-client feature. Doctor: existing
`launchers/dev/fiddler_parity_doctor.bat` or `.sh`. Development oracle needs
explicit hash-pinned private source; normal editing does not require Fiddler.
Contract: [reviewed WSC](../../../docs/editor/multi_fiddler_wsc.md).

1. Explicitly choose Fiddler WSC. Import two complete sections with decimal IDs
   1650 and 42, X/Y 120/80 and 123/82, Z 7 and -2, Color 21. Review must show
   Fiddler's normalized offsets 0/0 and 3/2, zero hue and default visible flags.
2. Enter dynamic ID `1650`: preview its flag 0. Enter `0x672`: disable Apply
   with a decimal-ID error. Restore `1650`, confirm, and save/reload the project;
   hidden visibility survives. Cancel must preserve the prior working copy.
3. Set hue 21 on the hidden part and raw flag 256 on the other. Direct export
   refuses loss. Reviewed export names the loss counts and disables confirmation
   until accepted. Output has tab-separated decimal fields, CRLF and Color 0;
   working hue/flags remain unchanged. Reopening has no stored flags unless a
   dynamic-ID set is supplied again.
4. Refuse ID 65535, incomplete/duplicate fields, overflow, oversized input/output,
   stale reviews and protected install/source destinations. Verify source hashes.
5. Run full smoke and assembly reload; require the multi completed marker and
   independent native read-back of its 151-component stage. Inspect the focused
   review at 1024/1366/1920 widths, including field-loss consent and invalid input.

```text
python tools/multi/test_fiddler_wsc.py
python tools/multi/test_fiddler_text.py
python tools/multi/test_multi.py
python tools/fiddler_parity/multi_wsc_reference.py --source <pinned-MultiComponentList.cs> --out <new-private-folder>
python tools/fiddler_parity/probe_multi_wsc.py --reference <Reference.dll> --out <new-folder>
python tools/fiddler_parity/proof_multi_wsc.py --reference <Reference.dll> --out <new-folder>
python tools/editor_smoke/run.py --reload --out <new-folder>
launchers/dev/smoke.bat
```

The focused Windows proof uses `--guo-editor-smoke <new-folder>
--guo-multi-text-proof` after Godot's `--`, as in the TXT/UOA recipe.
No map/world metadata, source installation write, renderer or settings-migration
certification is implied by the scalar proof.
