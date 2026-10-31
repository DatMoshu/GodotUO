# F24a reviewed Fiddler CSV

Layer: L3 manual editor and Tools; `#if TOOLS`, no AI dependency/client option.
Doctor: existing `launchers/dev/fiddler_parity_doctor.bat` or `.sh`.
Contract: [reviewed CSV](../../../docs/editor/multi_fiddler_csv.md).

1. Explicitly import Fiddler CSV with ID `1650`, X/Y/Z `12,-8,7`, flag `100`
   and extra `0x100`. Review must show item 0x1650, flag 256 and Unk1 256.
   Include duplicate rows with flag/extra `80000000`/`-2147483648`.
2. Save/reopen the project, edit/undo the extra through document history, copy/
   paste, save/place a stamp and rotate a one-to-one set. Preserve both raw
   fields and duplicate order. Refuse splits/joins/reorder under the existing
   tagged-entry transform policy. Check null-seventh/eighth-present projects.
3. Other text/legacy exports must refuse nonzero extra loss. Native staging must
   refuse extra alone even with ordinary 0/1 flags and create no stage directory.
4. Review without centering preserves coordinates. Centering changes X/Y only,
   retaining Z/flags/extra. Add hue 21: direct export refuses, reviewed export
   requires loss consent and retains working hue/extra. Cancel/stale review
   and protected source/install destinations must not mutate anything.
5. Check exact CRLF lower-case hex export against the pinned unchanged Fiddler
   reader/exporter in both directions. Test old empty/absent/decimal/hex Unk1,
   signed limits, malformed ID/extra, array alignment and byte/count limits.
6. Run full smoke and assembly reload. Require completed multi stage and native
   151-component independent read-back, with an unchanged installation. Inspect
   import and hue-loss review at 1024/1366/1920 widths.

```text
python tools/multi/test_fiddler_csv.py
python -m unittest discover -s tools/multi -p test_fiddler*.py
python tools/multi/test_multi.py
python tools/fiddler_parity/multi_text_reference.py --source <pinned-source> --out <new-private-folder>
python tools/fiddler_parity/proof_multi_csv.py --reference <Reference.dll> --out <new-folder>
python tools/editor_smoke/run.py --reload --out <new-folder>
launchers/dev/smoke.bat
```

Use the existing focused Windows multi-text proof for the six review captures.
No native extra-field translation, renderer or other CSV dialect is certified.
