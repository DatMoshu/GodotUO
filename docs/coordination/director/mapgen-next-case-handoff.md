# GUO-MAP-03 source package and next stair case

Local package completed for Director review. No integration, implementation
change, new runtime layout, commit, push, deployment or publication was performed.
The Director's main checkout remains untouched. Apply only in the designated
7474 candidate after the Editor package and a compatibility review.

## Exact source package

`build/coordination/map03/manifest.json` records each of the 27 tracked source
files, its base/content SHA-256, HEAD blob identity and byte count. The exact
tested delta is `a82ddb69805ba7c5b0265679b2ca3b6a3e6731c9` through
`78340f3debf978b67763498e4a9e241dc825e140`, branch `codex/cdda-uo-layouts`.

1. `tracked-source.patch`: exact retained integration diff, SHA-256
   `28b6686ded651475000eaa61c619168969db30c6ac1977eecccdd3cf878391fe`;
   27 files, 3,850 insertions and 5 deletions. Only tracked Python, C# and Markdown
   source is included. No raw game data, art, binary, local configuration or secret
   is included. Existing privacy rules and local deny values passed on all 27
   packaged HEAD files.
2. `map02-handoff.patch`: the later uncommitted MAP02 handoff update, kept
   separate from tested HEAD, SHA-256
   `f07add97254c3c9be8b301dac09bfc70c89291e92a753746daf9613c6fec3e09`.
   `map02-handoff.md` preserves the resulting document.

`package-verification.json` records patch checks and application in this order
against isolated exported base files under the new package directory. All 27
resulting source hashes match HEAD and the separately updated handoff hash matches
the snapshot. This verifies package integrity; it does not establish compatibility
with the Director's candidate or Editor changes. Review overlapping files before
applying there, particularly editor panels, editor smoke and data-format docs.

MAP02 acceptance remains 88/88 stops, zero room teleport tags and a real blocked
wall on tested HEAD. Its new continuous capture and build/archive identities are
in `build/layout_import/proof/guo-map-02-78340f3d/`; the separate handoff patch
records them. Media, retail archives and source-game extracts are not packaged.

## Small isolated unsupported stair case

`build/coordination/map03/stair-fixture.json` is an original synthetic semantic
fixture: two open 5×5 rooms, levels 0/1, paired central up/down connectors at
`[2,2]`, no partitions or furniture, default 20-z storey rise. It contains no
source-game extraction or art. `stair-boundary-report.json` records a direct
current-revision call to `native.reserve_stairs` without retail data:

```text
source stair at (2, 2) level 0: no bounded internal-flight adaptation fits
```

The rejection was reproduced, input remained unchanged, and no components were
emitted. Each axis has only five cells, so the six-cell straight run plus its
approach cannot fit. With no internal walls there is nothing for the bounded
partition-carving fallback to remove. This isolates footprint geometry from
furnishing, source decoding, catalogue selection and world placement. It is a
small reproducible boundary, not a claim of the smallest unsupported building in
the source census. The retained 72×72 market is a larger pending case and is not
the proposed next fixture.

## Proposed next scope, awaiting review

First propose a two-file importer change, without implementing a new stair form:

- `tools/layout_import/native.py`, `reserve_stairs`: preserve rejection and its
  existing message, adding actionable fixture/required-run context if desired.
  Do not silently shorten stairs, widen the footprint or remove exterior walls.
- `tools/layout_import/test_layout_import.py`, `AdapterTests`: promote the neutral
  fixture into a regression. Assert compact-footprint rejection before catalogue
  or component calls and unchanged inputs. Add a roomy control with matching
  upper connector and a synthetic catalogue, asserting the complete 20-z flight,
  approach, reserved upper opening and deterministic placement. Add missing-upper
  and missing-connector rejection controls. Test results must distinguish expected
  refusal from successful native conversion.

This first scope makes the unsupported mapping explicit; it does not convert the
compact fixture. Actual compact stairs require a separately reviewed turn or
switchback mapping. The exact likely files for that later geometry work are
`tools/multi/generate.py` (`staircase`, currently straight only),
`tools/layout_import/native.py` (planning/reservation/adaptation provenance), and
`tools/layout_import/test_layout_import.py` (geometry and circulation regressions).
The canonical generator is shared tooling, so its inclusion needs explicit scope
review before edits. Reuse the mined step/block catalogue and the existing
`tools/multi/validate.py` stair contract; validator changes are not proposed here.
Reject an unproven turn instead of bypassing validation or adding teleport routes.

Geometry acceptance would require complete 20-z climb, upper headroom, reachable
approach/arrival, preserved room circulation, no reservation overlap, stable
source-to-target provenance and an actual native climb/descent with wall collision
in a fresh proof directory. No layout growth, shared runtime edits or new live
proof is authorized in this packaging step. Existing MAP02 evidence is preserved.
