# Revalidate cover membership before discarding plans

## Scope and current status

Local candidate on `codex/cover-membership-validation`, based on exact world
sort commit `419e7a7a`. Layer L0, marked `PORT DEVIATION (GUO)`: preserve the
existing covering-land result while narrowing unnecessary retained-plan
invalidation. No rendering option, GPU/default change or external dependency.
This is separate from retained canvas commands and both GPU experiments.

The candidate passes 1,207 actual Release and 1,547 optimized Debug/Labs
checks. Full repository smoke exits 0 with aggregate `OK`; docs, launcher,
privacy and whitespace guards pass. Live stationary and walking raw-reference
checks have zero mismatches. Exact-route measurements show fewer plan rebuilds,
but no demonstrated general FPS gain. Retain as a local review candidate; it
is not a recommendation to merge or to enable either GPU experiment.

## Invariant

Every existing mutation hook still invalidates first-Land lookup immediately,
including non-Land movement. The old certified Land reference is retained only
as a bounded comparison baseline in the existing 64-cell array. A pending bit
records that the full chain must be recertified. Repeated invalidations cannot
replace that baseline with a newer identity.

Before any plan compares `CoverMembershipVersion`, pending cells are scanned.
The version advances when the first Land identity changed **or the chain lost
ownership certification**. A normal `GetFirstLand` before the version read
performs the same comparison, so it cannot accidentally bless an old plan.
Uncertified cells with no baseline remain conservative. Full cache invalidation,
reload and pool reuse unconditionally advance the version and release references.

A Land-only mutation shortcut would be wrong. The retained tile anchor can be
a mobile or Land; detaching or moving that anchor can change which Land is
reachable or expose a foreign chain. Every mutation hook and full-chain
certification remains necessary. Map/table identity, absent-slot dependencies,
admission limits and eviction behavior are unchanged.

Only identity discovery is retained. Roof breaks, live geometry/alpha/depth,
cover winners, occurrence order, transparency and picking are not cached by
this change. Pending references are bounded by existing chunk storage and are
released when resolved or fully invalidated. The first dependency read may now
scan dirty cells unrelated to that individual plan; this is a possible cost,
not an assumed optimization win.

## Correctness evidence

The new C# fixture adds 25 checks for benign mobile insertion/movement/unlink,
XYZ changes before unlink, two-cell invalidation, repeated pending changes,
first-Land replacement, non-first Land changes, certified null, lost ownership,
Land anchors, mutation during discovery, live visual fields, full invalidation
and a zero-allocation deferred scan. Existing anchor/foreign-chain, pool ABA,
map replacement, missing-slot and capacity fixtures remain active.

A previous fixture invalidated a cell without changing anything and required
rejection. It now inserts a genuinely different first Land during discovery;
a new fixture separately proves benign discovery can publish. A supplemental
Python state model passes 648 transitions and 100,000 seeded operations with
102,958 plan checks. The model is not a C# or pixel test.

Both baseline and candidate same-client captures alternate cache off/on with
two completed draws before each image. All four world scenes exercise cache
hits and match exactly. Cross-launch raw comparisons initially differ by bank
10,498, forest 285, dungeon 90 and docks 4,401 pixels. An unchanged baseline
repeat reproduces those differences exactly and then matches the candidate
pixel for pixel at login and all four world scenes. The initial differences,
unchanged control and all captures are retained. This is recurring launch
variation, not evidence that the cover patch changed the image; its underlying
cause remains unresolved. Similar variation also occurred before this patch.

Live stationary verification checks 1,449,866 plans / 46,006,312 references at
bank and 1,091,856 / 32,603,328 at docks, with zero mismatches. Exact 46-tile
walking checks add 1,662,300 / 56,444,721 at bank and 1,487,945 / 42,797,180 at
docks, again zero mismatches. Both routes finish every point with matching
requests/acknowledgements, no pending request and post-completion draw checks.
Walking verification uses a 15-second warmup and the expensive raw oracle;
its timing is diagnostic, not a performance result.

These checks cover the exercised states, not every possible live world or
device combination. Full source mutation-path review is preserved in the
private preparation report.

## Measurement contract and reasons for caution

Timing uses actual exported Release, ordinary canvas, 2560×1440, zoom 2,
retained canvas off, GPU modes off, uncapped and VSync off. The raw verifier
is disabled. Standing samples discard 60 seconds, then measure 30 seconds.
Travel uses the existing exact 46-tile bank/docks routes after a 60-second
warmup, including their documented 250 ms release cadence. Other projects
remain active; host load and atlas work are recorded.

There was little standing rebuild work to save in the preceding sort cohort:
bank had 248 plan builds over 2,973 draws, docks zero over 3,048. Dependency
checks were 7,818,902 and 5,026,152 respectively. Earlier moving observations
had 3,455 builds / 1,557 draws at bank and 1,818 / 2,156 at docks; many misses
can be new visible regions rather than membership invalidation. A higher hit
rate alone cannot establish reduced frame cost. Review the added version-read
cost as well as avoided discovery.

The private native-engine compile started at 08:41:21.975847 UTC before its
worker acted on the requested hold. Samples overlapping that compile must be
marked as under our own build load and repeated before interpreting them.
No CUO benchmark is part of this cohort.

Frozen exports: `windows-world-sort-adaptive-v2` before and
`windows-cover-membership-v1` after. Managed DLL SHA256:

- Before: `105cec88fdca4d688e90b5e8506c67bb0f28a1ef3603be651ba1ca5b6a14a24e`.
- After: `449854e5da5e3d3a217cd42bd0f0dcacfa8c93701217e2381f8fac87c789658b`.

## Measured results

All results below ran under other projects' load. The first bank standing run
also overlapped our private native compile and is retained as contaminated:
81.296 FPS, 12.301 ms mean, 18.714 ms p95. Its repeat after compilation was
72.513 FPS, 13.791 ms mean, 19.924 ms p95; whole-run host CPU averaged 49.8%,
peaking at 94.2%, and four atlas uploads occurred. Removing our compile did
not make the machine otherwise idle. Docks standing measured 117.449 FPS,
8.514 ms mean, 12.485 ms p95, host CPU mean 38.6% and one atlas update.
These standing observations are not a matched before/after FPS estimate.

The travel pairs complete identical 46-tile routes, including heights, facing
turns, 250 ms release cadence and acknowledged movement. Both start unmounted,
hidden, alive and running, with 100 stamina and the same speed settings.
Bank completes 50/50 requests/acks; docks 55/55. No pending request, dropped
point or failed completion drain remains. Live object populations, time per
position, atlas work and host load still vary between launches.

| Scene | Before / after FPS | p95 ms | Prepare ms | Draw ms | KB/frame | Plan builds/draw |
|---|---:|---:|---:|---:|---:|---:|
| Bank travel | 69.147 / 70.602 | 20.240 / 20.859 | 5.840 / 5.887 | 4.421 / 4.295 | 24.0 / 19.7 | 2.539 / 0.620 |
| Docks travel | 97.999 / 95.762 | 14.904 / 16.064 | 3.847 / 4.015 | 2.957 / 3.101 | 12.5 / 12.1 | 0.634 / 0.129 |

Bank plan builds total 3,926 / 977 over 1,546 / 1,575 draws; docks 1,458 / 288
over 2,300 / 2,237. This is approximately 76% / 80% fewer rebuilds per draw.
It does not prove the same reduction in preparation cost: measured preparation
and both p95 values are slightly worse. Bank first-Land scans fall from 2.4 to
0.4 per frame; docks are 0.7 to 0.9. The getter still performs millions of
dependency reads, and pending scans can touch cells unrelated to one plan.

Bank atlas updates differ 78 / 35; docks 71 / 46, each submitting a full
16 MiB page. Whole-run host CPU means are bank 37.5% / 51.4%, docks 39.0% /
41.3%. A second native reporting build ran 08:50:34–08:51:08 UTC during the
docks-before client lifetime (08:50:00–08:51:49); that run is also flagged for
our build load. The existing interval output has relative scene time, so do
not infer exact overlap with its measured interval from process timestamps.
The main-thread allocation difference is real for these observations but not
an isolated estimate of this patch's contribution, given different atlas work.

Keep this bounded identity-validation candidate for review because it reduces
unnecessary reconstruction without changing the exercised results. Do not
claim it solves the remaining 4–6 ms preparation cost or reaches the 4 ms whole
frame target. Native attribution and a narrower static-preparation design are
the next larger targets. No push, merge or renderer default change follows.
