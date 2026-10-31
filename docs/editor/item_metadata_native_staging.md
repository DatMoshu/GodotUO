# Verified native item metadata staging

F06b adds **Export verified item metadata stage** to Assets → Art → Statics.
It captures the selected working document after validating pending fields.
**Save copy** continues to save project JSON; native export is a separate review.
This is L3 editor authoring plus Tools, with no AI dependency or runtime change.

Choose a staged folder and **Prepare verified copy**. The review shows current
staged and exported names, complete 64-bit flags and heights. Preparation alone
does not publish the candidate. **Apply verified metadata** opens a separate
confirmation, independently rechecks the candidate, then promotes the staged
copy. **Recover previous stage** restores the prior stage or its absence while
retaining the candidate separately. Closing during preparation cancels its owned
process. The source installation stays read-only.

The working document may change flags, height and name. Native staging preserves
the destination's unexposed weight, layer, count, animation, hue and light fields.
Unchanged names retain their exact original bytes, including padding after NUL;
changed names are NUL-free UTF-8, at most 20 bytes, with zero padding. Source,
saved-copy, pending-draft and recovery changes require a fresh review. A stale
recovered draft is retained and blocks saving/export until explicitly reverted
or discarded through **Reopen item metadata**. Reopen confirms discarding only
that item's draft and recovery. Its saved project document is retained.

Native output supports existing modern 41-byte item records (7.0.9.0 onward).
It does not allocate IDs or edit land/old-layout tiledata. A `verdata` item-group
patch that would win over the selected native record is refused explicitly;
unrelated patches remain unchanged. This does not certify writing legacy patches.
The editor refuses a destination supplying its currently loaded staged data.

The Tools workflow is available without Godot:

```text
python tools/fiddler_parity/metadata.py prepare --source <install> --stage <stage> --edits <metadata-edits.json> --plan <new-plan-folder> --client-version <version>
python tools/fiddler_parity/metadata.py apply --plan <new-plan-folder>/plan.json --confirm
python tools/fiddler_parity/metadata.py recover --stage <stage> --confirm
```

The inert edits JSON has schema 1, domain `item-metadata`, and exactly one
`records` entry with integer `id`, `source_record_base64` and `record_base64`.
Each payload is 41 bytes. The source must match the installed record, and bytes
8–19 in the document must remain unchanged. The adapter preserves these bytes
from the current stage when merging the three editable fields. Inputs are
bounded to 128 KiB and native tiledata to 65,536 item records.

The existing typed `tiledata-item` writer accepts an exact 41-byte payload with
empty field metadata and explicit replacement. This avoids the legacy field
writer's Latin-1 name conversion. The independent pinned ClassicUO
`TileDataLoader` checks all decoded fields and the complete raw record. The
verifier also checks every untouched byte, land record, group header and file
length. Apply checks the source file/patch context, edits, baseline, candidate,
expected manifest and prior-stage hashes before locked recoverable promotion.

Run `launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`) for the read-only
oracle. Synthetic cases live in `tools/fiddler_parity/proof_metadata.py` and
`tools/uodata_write/test_metadata_records.py`; the repeatable editor recipe is
`tools/scenarios/fiddler-f06b/README.md`. Editor smoke covers native document
guards and recovery, and the windowed Assets proof drives prepare, confirmation,
Apply, recovery and cancellation at three editor widths.
