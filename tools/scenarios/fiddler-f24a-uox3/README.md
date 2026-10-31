# F24a UOX3 export certification

Layer: L3/Tools; no client toggle or AI dependency.
Doctor: `launchers/dev/fiddler_parity_doctor.bat` / `.sh`.

1. Open two identical components at 12,-8,7, including hidden/opaque flags and
   hue 21. Direct export must refuse. Reviewed Fiddler UOX3 export must show
   records and require field-loss consent. Export keeps working fields intact.
2. Check uppercase IDs, decimal offsets, CRLF, no indentation, sequential block
   numbers and a blank line after each block against the pinned exporter.
3. Cancel/stale-document/protected destinations create no output. Nonzero CSV
   Unk1 is refused. Import menus/F3 expose only the legacy UOX3 reader; explicit
   programmatic import of the export-only profile returns guidance.
4. Capture export review at 1024/1366/1920 and inspect. Run full smoke and
   assembly reload; require completed native staging/read-back and unchanged
   installation. Existing TXT/UOA/CSV reference proofs must remain passing.

Commands: `python -m unittest discover -s tools/multi -p test_fiddler*.py`,
`python tools/multi/test_multi.py`, `proof_multi_uox3.py --reference <Reference.dll>
--out <new-private-folder>` under `tools/fiddler_parity`, full smoke and existing
editor reload/focused text proof. Reference construction uses explicit pinned
source; no Fiddler import, server or renderer parity is claimed.
