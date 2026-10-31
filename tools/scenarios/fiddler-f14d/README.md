# F14d native sound staging

Layer: L3 manual editor and Tools; compiled under `#if TOOLS`; no AI gate needed.
Toggle: no exported-client feature. Doctor: existing
`launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`).
Contract: [native sound staging](../../../docs/editor/sound_native_staging.md).

1. Use disposable synthetic MUL/IDX and uncompressed UOP libraries with two
   occupied sounds, opaque index words and 12-byte UOP entry headers. Add a
   `Sound.def` alias to the selected occupied row. Keep source hashes.
2. Prepare a reviewed name/PCM replacement, independently read it through pinned
   ClassicUO and assert the alias mapping, name fingerprint, PCM hash and samples.
   Preserve every other record/header and exact original container prefix.
3. Cancel Apply: the destination remains unchanged. Confirm Apply, retain unrelated
   prior stage content, then recover the exact complete prior stage or its absence.
4. Corrupt PCM, name, another record, index extra, UOP header or checksum: refuse.
   Change source/DEF/working PCM/snapshot/candidate/baseline/review/prior stage:
   refuse stale Apply. Refuse empty rows, implicit replacement, duplicate IDs,
   compressed archives, links, unregistered collisions and overlapping outputs.
5. Run the headless editor reload, including native source/saved-copy/draft and
   selection guards, then the windowed Assets workbench proof. Review current and
   export sound at 1024/1366/1920 widths; audition each, cancel/confirm Apply,
   recover, and close during owned preparation. Verify source hashes unchanged.

```text
python -m pytest tools/uodata_write/test_sound_records.py tools/fiddler_parity/proof_sound.py -q
python tools/editor_smoke/run.py --reload --out <evidence-output>
python tools/editor_smoke/run.py --windowed --assets-workbench --out <evidence-output>
launchers/dev/smoke.bat
```

Real-library certification must name the client version and packaging separately;
synthetic MUL evidence does not establish a real MUL installation proof. Runtime
deployment, empty-slot allocation, deletion and music editing remain separate.
