# Verified native texture staging

F09c extends the texture working document into a native staged-copy workflow.
This is L3 editor authoring plus Tools; the source installation remains read-only.
The texture inspector's **Export verified texture stage** action captures the
working document. Preparation shows current/candidate previews and the complete
affected-ID list in a scrollable field. Apply requires a separate confirmation;
recovery remains available after promotion. Changing the destination invalidates
the prepared plan. Source/project/document changes require a fresh review.

```text
python tools/fiddler_parity/textures.py prepare --source <install> --stage <stage> --edits <texture-edits.json> --plan <new-plan-folder> --client-version <version>
python tools/fiddler_parity/textures.py apply --plan <new-plan-folder>/plan.json --confirm
python tools/fiddler_parity/textures.py recover --stage <stage> --confirm
```

Run `launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`) to build the
independent read-only ClassicUO oracle. Native textures use MUL/IDX; this story
does not claim other asset formats or container conversions.

The inert edits JSON has schema 1, domain `texmap`, and exactly one `records`
entry: integer `id`, `source_record_base64` and `record_base64`. Both payloads
contain the complete 8,192-byte 64-square or 32,768-byte 128-square native record.
The source payload must match the effective installed texture. JSON is bounded
to 128 KiB. Raw bytes retain bit 15, opaque black and original quantization;
there is no native-to-PNG-to-native conversion during staging.

`TexTerr.def` can copy index entries in file order. The pinned upstream parser
and texture loader establish the requested ID's physical source row and every
affected visible alias. For example, `0 { 1 }` followed by `1 { 2 }` leaves ID 0
pointing at physical row 1 while ID 1 points at physical row 2. The review must
show the physical row and all affected IDs before Apply. Existing registered
stage definitions take precedence over the install. Unregistered loose stage
definitions are excluded from the oracle, matching the exported override list.

Preparation snapshots the effective pair and definitions into a private plan,
copies the previous stage, and calls the shared typed native writer. It appends
the changed record and repoints only the physical row, preserving its four-byte
extra word. Identity replacements append nothing. The independent proof checks
the exact old MUL prefix, exact append bytes/length, every untouched index byte,
full 16-bit raw hash, opaque RGBA pixels, dimensions and alias mapping.

Apply rechecks the install, source-definition presence, edits, baseline,
candidate, manifest and prior stage before independent read-back and locked
promotion. The previous stage and recovery journal use the existing static/hue
transaction. Recovery restores the prior stage or its absence and retains the
candidate separately. Preparation alone never publishes a stage. No free-slot
allocation, deletion, source-install writes or automatic pack activation occur.

Synthetic proofs live in `tools/fiddler_parity/proof_texmaps.py` and
`tools/uodata_write/test_texmaps.py`. They cover sizes, high bits, opaque zero,
DEF order/group handling, alias effects, no-op identity, corruption, stale inputs,
apply/recovery and exact preservation. These checks do not substitute for a
real-library staged export and native editor review.
