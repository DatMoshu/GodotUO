# Fiddler parity: independent static-art read-back

## Native hue staging (F08f)

The same identified oracle now also reads complete hue groups through upstream
`HuesLoader`. `hues.py` adds reviewed native hue candidates using the existing
typed `uodata_write` writer and shared recoverable stage promotion. See
[native hue export](../../docs/editor/hue_native_staging.md) for the CLI,
source bindings, file limits and the isolated reader's radar-input alias.
Build with the existing doctor, then run `proof_hues.py` explicitly alongside
the static-art proofs. This certification does not imply native sound, Cliloc,
tiledata, texture or animation writing.

F01a is a read-only Tools adapter. It compiles `ClassicUO.Assets` through the
same project boundary as guoasset, then loads **only art**, directly from an
explicit stage. It does not start MCP, Godot, a shard, AI, or the full client
installation loader. No third-party code is copied. Upstream retains its BSD
notices; its checkout stays read-only, with build artifacts under Config.build.

## Dependency doctor and proof

Install the .NET 10 SDK and obtain the project's existing ClassicUO source
checkout using `launchers/dev/sync_upstream.bat` (or `.sh`). Configure
UO_BUILD_DIR outside the checkout, then:

```text
launchers/dev/fiddler_parity_doctor.bat --build
python -m pytest tools/fiddler_parity/proof_static.py -q --basetemp <scratch>/f01a-proof
```

The doctor also has a `.sh` launcher. Without `--build`, it checks the already
built oracle and reports its identity/runtime. Missing dependencies fail with
an actionable doctor instruction. The integration proof is explicitly invoked,
not silently skipped or added to the dependency-free pooled Python suite.
Pytest owns and cleans its basetemp directory: use a dedicated scratch path.

The build embeds the clean source commit and a SHA256 of the Assets, IO and
Utility C# source trees. The report records those, the adapter binary hash,
and the runtime version. A build without identity is refused. Our first
tested source is local ClassicUO revision
`007ef8c3e13dec13fcc02387fe4817ef6f371c85`, which includes the project's
loose-art extensions. Loose `Art` overrides are refused by this runner.
The oracle remains independent of GUO's Python encoders/writers; the pinned
reader is not represented as a pristine public-release build.

## Verify a stage

```text
python tools/fiddler_parity/run.py verify --source <original> --stage <stage> --manifest <expected.json> --out <report-folder>
```

`--oracle` can select an explicit adapter DLL; by default it is
`Config.build/fiddler-parity/bin/Oracle/release/guo-static-oracle.dll`.
Source and stage are separate, non-nested directories containing the art
container. No setting can substitute an installed dataset for either input.
Output must not overlap either directory or the expected manifest. Links and
hardlinked container files are refused. Reports never contain copied artwork.

Manifest schema 1 is inert JSON:

```json
{
  "schema": 1,
  "packaging": "mul",
  "client_version": "7.0.107.76",
  "records": [
    {"id": 2, "width": 1, "height": 1, "rgba": [4278190335],
     "raw_sha256": "<SHA256 of the intended encoded record, including header/padding>"}
  ]
}
```

IDs are static IDs, without the 0x4000 container offset. `rgba` is a row-major
array of unsigned RGBA8888 integers, R in the low byte. Expected pixels must
come from an independently specified fixture or the intended source image,
never GUO decoding its own encoded record. SHA256 covers the entire expected
raw payload. Limits: 256 IDs, 1024x1024 per image, 16 MiB manifest, 512 MiB per
container file. The report directory contains `report.json` (schema 1):
outcome, layout, oracle identity, checked IDs, expected/actual mismatches,
input hashes, preservation counts, unsupported cases and inputs_unchanged.
Exit 0 means pass; exit 1 means mismatch, unsupported or recorded reader error;
exit 2 means invocation/manifest/path failure. No missing oracle is a pass.

The runner checks container and RLE bounds before invoking upstream's unsafe
decoder. It hashes source, stage and manifest before/after every verdict.
Untouched record payloads, MUL extra fields and empty index entries are compared
separately from edited pixels. Container offsets may legally move; whole-file
identity is **not** asserted for a changed stage.

## Replace existing static art

The editor's existing **Replace from image** action imports and previews the
project PNG. A replaced static now offers **Export verified static stage…**.
Choose a stage path, prepare its verified copy, then explicitly apply it.
Preparation alone never changes the current stage. The window belongs to the
editor plugin and cancels its child process on teardown; manual work needs no AI.

The same Tools workflow is available without Godot:

```text
python tools/fiddler_parity/replacement.py prepare --source <install> --stage <stage> --image <edited.png> --id 0x0E75 --plan <new-preview-folder> --client-version 7.0.107.76
python tools/fiddler_parity/replacement.py apply --plan <new-preview-folder>/plan.json --confirm
python tools/fiddler_parity/replacement.py recover --stage <stage> --confirm
```

The single-record workflow replaces **one occupied static ID**, in MUL/IDX or an
uncompressed art UOP, preserving opaque per-entry headers and the original four-byte
static header and untouched records. Encoding can change edited-record padding
and RLE layout; quantized pixels and intended payload are independently checked.
PNG alpha below 128 becomes transparent; opaque black becomes near-black.
No deletion, free-ID allocation, compressed UOP or verdata
install is added by this workflow. Source file names use canonical container
names; unusual case-sensitive dataset layouts need a separate adapter.

Plans hold an inert versioned manifest, original/current art snapshot, candidate,
and independent reports. Apply rejects changed inputs/candidates, rechecks the
candidate and serializes promotion with an OS-held stage lock. Candidate and
stage must be on the same filesystem for directory rename. The old stage stays
at a uniquely named sibling; a flushed recovery journal records promotion.
Ordinary promotion errors restore the old stage. After process interruption,
Recover restores it without deleting the candidate. For a first-ever stage,
Recover restores the prior absence and retains the candidate separately.
Recovery is a local filesystem operation and cannot undo external changes made
by another tool after promotion; inspect any such changes before choosing it.

## Certified boundary

- Static art in MUL/IDX and **uncompressed** LegacyMUL UOP, independently.
- Direct synthetic decode and free-slot insertion through the existing writer.
- Transparent pixels, near-black, nonzero unknown header and trailing payload.
- Changed pixel, wrong ID, invalid offset, truncation, altered untouched record,
  altered header and invalid row-offset negative controls.
- F01a alone establishes no occupied replacement. The following F02a adapter
  adds synthetic occupied replacement, stale-input rejection, failed-promotion
  rollback, interrupted-promotion recovery and exact prior-stage restoration.
- Opaque UOP per-entry headers are copied byte-for-byte, including changed IDs.
  Verification separately checks header bytes and untouched entry metadata;
  the independent reader checks edited pixels and payload hashes.
- No compressed UOP, gumps, land, tiledata, animation or verdata certification
  follows from these static-art proofs.

`proof_uop_headers.py` adds literal opaque-header fixtures, target/untouched
header corruption, untouched-checksum corruption and invalid-header ranges.
Read-only hardlinked source files are copied into fresh unlinked candidates;
hardlinked stage outputs remain refused. The local private run
`20261020_guo-worker-fiddler-parity_uop-entry-headers` also prepared, verified,
applied and recovered a disposable two-record stage from the configured
7.0.107.76 installation, preserving 42,528 untouched records and all 42,530
entry headers. The install and its hardlinks remained unchanged. This is a
ClassicUO-oracle proof for that dataset, not every third-party client.

The synthetic fixture specification is in
`tools/scenarios/fiddler-f01a/README.md`.

F03a adds a [batch static PNG review](../../docs/editor/batch_static_replacement.md)
with filename-ID validation and one candidate/verify/promote transaction for
all selected records. `batch.py scan` reports every PNG error without writing;
`batch.py prepare` refuses stale scans and delegates to the same independently
verified stage adapter. `proof_batch.py` covers the additional batch contracts.
