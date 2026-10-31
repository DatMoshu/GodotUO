# Verified native hue export

F08f is manual L3 editor authoring and Tools validation. Moshu authorized the
recommended sequence and native export testing on 2026-10-20. Output is a
separate staged copy. GUO never writes into the client installation.

From a hue's inspector, commit or undo pending fields, then choose **Export
verified hue stage**. Prepare captures the complete working palette, raw bounds
and native 20-byte ASCII name. A long or non-ASCII project name shows its exact
native conversion before application; the full name stays in the project.
Preparation validates the document's original 88-byte source record against
the on-disk library, copies the effective staged file into a private baseline,
uses `uodata_write`'s typed hue record writer, and independently reads the
candidate through the pinned ClassicUO loader. Review the actual staged before
and intended after palette, then explicitly apply. Preparation alone does not
replace the destination. Existing stages retain their unrelated files and other
hue edits. The old stage is kept for explicit recovery.

The format is `hues.mul`: each 708-byte group has a four-byte opaque header and
eight 88-byte hue records. Each record contains 32 unsigned 16-bit colors, two
unsigned 16-bit bounds and 20 raw name bytes. IDs are one-based. No allocation,
removal, group growth, compressed container or verdata overlay is certified.
Unchanged names retain all original padding bytes. Colors retain bit 15.

The oracle is the existing `tools/fiddler_parity/Oracle.csproj`, built through
`launchers/dev/fiddler_parity_doctor.bat --build` (and its `.sh` counterpart).
It calls upstream `HuesLoader.Load`, not GUO's writer/decoder. The upstream
loader also reads radar colors; the isolated hue oracle aliases that unused
read-only input to the same even-length hue file. Radar data does not participate
in this proof. The report includes upstream commit/source hash, oracle binary
hash, raw records and preservation counts. Exact target records, group headers,
all other records and file length are checked independently. Missing readers and
malformed groups fail; a green generic smoke is not native certification.

The CLI accepts an inert schema-1 edit manifest with `domain: "hue"` and
`records` containing `id`, `source_record_base64` and `record_base64`:

```text
python tools/fiddler_parity/hues.py prepare --source <install> --stage <stage> --edits <edits.json> --plan <new-plan> --client-version 7.0.107.76
python tools/fiddler_parity/hues.py apply --plan <new-plan>/plan.json --confirm
python tools/fiddler_parity/hues.py recover --stage <stage> --confirm
```

Prepare accepts 1–256 existing IDs and at most 256 KiB of edits. Inputs are
snapshots, not executable manifests. Stage/source/plan/input paths must be
disjoint; output traversal, link aliases and hardlinked staged files are refused.
Apply rechecks source, input, candidate, manifest and previous-stage hashes under
the shared OS-held stage lock, repeats independent read-back, and uses the same
flushed promotion journal and recovery mechanism as static-art export. Canceling
a preparation stops its owned child process. An interrupted promotion can leave
a journal requiring recovery; closing the window does not silently undo an
already completed apply. Do not edit a stage externally before recovery.

Run `python -m pytest tools/fiddler_parity/proof_hues.py -q` with the oracle
built. The scenario is `tools/scenarios/fiddler-f08f/README.md`. Evidence must
distinguish synthetic proof, real-library disposable-stage read-back, and the
native editor review/apply workflow.
