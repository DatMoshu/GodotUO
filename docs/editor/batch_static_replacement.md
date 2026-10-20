# Batch static PNG replacement

F03a, L3 editor authoring and Tools. This extends the owner-approved occupied
static-art workflow to one reviewed batch. The installation remains read-only.
It adds no land/gump replacement, deletion, binary metadata output, compressed
UOP support or client feature.

Open **Review batch static PNG replacements** from Art's filter row or Bulk.
Choose a PNG folder and staged-data destination, then **Scan folder**. The table
shows each filename, resolved item ID, dimensions and error or replacement
action. Select a row for installed-source and folder-image previews. The PNG
preview is before UO 15-bit quantization; independent verification checks the
quantized pixels actually written.

Names are decimal (`26.png`) or explicitly hexadecimal (`0x001A.png`), with
GUO's optional prefix (`static_0x001A.png`). Leading zeros remain decimal.
Unprefixed hex names such as `001A.png` are reported as errors rather than
guessed. IDs must be unique even when spellings differ. Only top-level PNGs
are considered; other entries are counted as ignored. Every PNG error blocks
preparation. A batch contains at most 256 PNGs and 1,000,000 source pixels,
with each image no larger than 1024 × 1024.

Linked/hardlinked inputs, missing or empty IDs, malformed containers,
source-layer verdata and unsupported UOP layouts are refused. Uncompressed,
zero-entry-header art UOP and MUL/IDX are certified separately by the existing
independent ClassicUO oracle. An existing stage must belong to the selected
installation and contain complete art containers when it overrides them.

**Prepare verified batch** rescans the folder and rejects a stale preview,
builds one separate candidate, preserves each changed record's unknown header,
and independently verifies every target's pixels and all untouched records.
UOP batches scan the entry table once. Scan, preparation, refusal and
cancellation during preparation do not change the current stage.

**Apply verified batch** asks for confirmation, checks every selected PNG,
source, stage and candidate hash again, reruns the oracle, then promotes the
whole candidate. The previous stage and promotion journal support whole-stage
recovery. If cancellation interrupts promotion, use **Recover previous stage**
before retrying. Changing either path invalidates scan/preparation. Closing
the dialog or reloading the addon cancels its child process. Project image
overlays and metadata documents are unchanged.

CLI equivalents:

```
python tools/fiddler_parity/batch.py scan --folder PNGS --source INSTALL --stage STAGE
python tools/fiddler_parity/batch.py prepare --scan SCAN.json --plan NEW_PLAN --client-version 7.0.107.76
python tools/fiddler_parity/replacement.py apply --plan NEW_PLAN/plan.json --confirm
python tools/fiddler_parity/replacement.py recover --stage STAGE --confirm
```

Save scan stdout verbatim as `SCAN.json`. Dependencies and failures name
`launchers/dev/fiddler_parity_doctor.bat --build` (or its `.sh` twin).

`proof_static.py` and `proof_batch.py` run with pytest and synthetic containers
against the independent pinned reader. They cover both containers, untouched
records, headers, collisions, empty IDs, malformed PNGs, stale previews,
per-image rechecks, cancellation and recovery. `editor_smoke --reload` checks
dialog states/lifecycle; `--windowed --assets-workbench` captures the batch
review alongside the workspace and single-record review. Real-install
replacement and interactive folder picking are not inferred from those checks.
