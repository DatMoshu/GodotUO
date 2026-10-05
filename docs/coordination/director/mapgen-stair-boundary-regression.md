# GUO-MAP-04 stair boundary regression handoff

The approved test-only batch passes all 29 importer tests, up from 25. It adds
four tests to `tools/layout_import/test_layout_import.py` on branch
`codex/cdda-uo-layouts`, baseline
`78340f3debf978b67763498e4a9e241dc825e140`. The implementation, existing rejection
messages, canonical generator and validator remain unchanged. No live proof,
layout growth, integration, commit, publication, push or deployment was performed.
Director/main checkouts were not edited.

## Behavior checked

The tests call the real `native.reserve_stairs` planner. The roomy control also
runs the real canonical `G.staircase` component generator and `G.Built`; only
retail catalogue lookups are replaced with a neutral two-item synthetic catalogue.

- Two original open 5×5 semantic rooms with central paired connectors refuse
  conversion with the exact existing message:
  `source stair at (2, 2) level 0: no bounded internal-flight adaptation fits`.
  Inputs remain unchanged, no components are emitted, and neither catalogue
  lookup is called. This is expected refusal, not successful native conversion.
- A 9×9 two-level control produces one straight 20-z flight from z=7. Its four
  real emitted steps have z values 7/12/17/22; ten support blocks include the
  four-block landing. The approach and arrival are outside the five reserved
  opening cells. The upper opening reservation preserves the arrival and a route
  to the authored upper connector; both floors retain connected circulation.
  Adaptation lineage points to the original neutral connector. Reversing cell and
  room-cell ordering produces identical reservations, flights, adaptations and
  emitted components.
- Missing upper level and missing upper connector refuse with their exact
  existing messages, without mutating inputs, emitting components or consulting
  the catalogue.

The roomy fixture places its upper connector at `[7,7]`, outside the prospective
opening; its lower connector is `[4,4]`. These planner tests check reservation,
not full compiler floor cutting or gameplay. A source upper connector that lies
inside the chosen opening is outside this positive control. No new compact or
turning stair form is implemented or claimed.

## Portable patch and exact identity

Package: `build/coordination/map04/`. Apply only the test patch after checking the
candidate's test file against the baseline; the earlier MAP02/MAP03 documentation
changes in this checkout are not included in it.

- `stair-boundary-tests.patch`: one changed file, SHA-256
  `ee79289c8a722b46144cf5c183bf0f3c3bbdb565dae27f322cadd51b55b3e224`.
- Baseline test file Git blob: `532358132dcc420daf2c0ac4f34860abc7d6bccf`.
  SHA-256 `d2564f2c548f0a77f1a688aa6282f8b57907171586a290ae89c6af13b5eb91b8`.
- Updated test file SHA-256:
  `6362b7359372524af9e735b7dd7b8fdc63da6378a28809f6daf74ab3a98f99a8`.
- `manifest.json`: baseline revision, patch/content identities, four added test
  names, 25→29 acceptance count and unchanged implementation hashes.
- `importer-suite.log`: 29 tests in 2.655 seconds, exit 0, SHA-256
  `6b95c936f4601ab0915c1477808e3ea7335164866aa96cef5cb8938760d3f25a`.
- `test_layout_import.py`: exact updated source snapshot for review.

The patch passed `git apply --check`, was applied only to an isolated exported
baseline under `build/coordination/map04/apply-check`, and reproduced the updated
test file byte for byte. This verifies patch integrity rather than candidate
compatibility. The test patch contains original neutral fixture code, no raw game
data, assets, personal configuration or secrets.

Reproduction:

```powershell
python -m unittest discover -s tools/layout_import -p test_layout_import.py -v
```

The first restricted full-suite attempt encountered sandbox temporary-directory
access errors in existing filesystem/database tests. The accepted rerun set
`TEMP` and `TMP` only for its process to a workspace-owned `map04/tmp` directory;
no test implementation, persistent configuration or ACL was changed. The four
new planner tests also passed independently in the restricted environment.

No full GUO smoke/reload or gameplay capture was rerun for this test-only batch;
Director owns combined candidate reload. MAP02 runtime acceptance remains the
separate retained evidence. Review/apply this patch in the designated candidate
and rerun the importer suite there before treating it as candidate acceptance.
