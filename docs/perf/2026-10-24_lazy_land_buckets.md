# Allocate loose land queues only when used

## Scope

Local L0 allocation candidate, based on `0c0e8a5b`, marked `PORT DEVIATION
(GUO)`. Ordinary Release uses the same renderer, command order and live UI
values. There is no new option or dependency; GPU experiments remain separate.

Every fresh `RenderLists` previously constructed empty normal and stretched
Land lists, including temporary UI child lists that never contain Land. These
two buckets now allocate on their first actual Land insertion. Clear preserves
an allocated bucket and tolerates an absent one. Transparent Land keeps its
existing path. Normal and stretched buckets stay independent per owner.

`DrawRenderList` must still reset `CurrentObjectId` for an absent bucket, just
as the former empty list did. The loop is conditional; its final reset remains
unconditional. Zoom diagnostics report zero for these two known absent lists.
No callback, clipping, child traversal, deferred dimension, texture lookup,
alpha, depth or exception behavior is changed. No child-list pool or persistent
control cache is introduced.

## Checked allocation effect

Actual exported Release passes 1,217 checks; optimized Debug/Labs passes 1,557.
The ten additional checks exercise lazy construction, UI-only use, repeated
Clear, first normal/stretched Land, separate nested owners, retained bucket
identity and null/empty draw ID resets. Existing typed UI checks still cover
live owner dimensions, clipping, ordering, callbacks and repeated flushes.

The existing allocation fixture constructs 100 fresh lists at each population.
Both its legacy-callback and typed-command paths save exactly 6,400 bytes at
each measured population (0, 1, 2, 3, 8, 20, 32 commands). That is 64 bytes per
fresh list on this runtime, not an architecture-independent size assertion.
Empty construction falls from 320 to 256 bytes. Warmed typed enqueue remains
zero allocation. Earlier typed-command growth tradeoffs at 3 and 20 commands
remain; this change does not remove their extra array growth.

Baseline DLL SHA256:
`449854e5da5e3d3a217cd42bd0f0dcacfa8c93701217e2381f8fac87c789658b`.
Candidate DLL SHA256:
`6dedbcc62e908412f414b5301a7c17a0f15e27418c78a701e99dd296041461b4`.

Full smoke passes, including the editor checks. No source changed after those
checks and the frozen export. Live pixel comparisons are recorded below.

## Live Release samples

Ordinary exported Release, 2560×1440, zoom 2, live world, both GPU modes and
retained canvas off. Each scene discards 60 seconds then records 30 seconds.
Run order is bank before/after, docks after/before. Other projects remain
active; host-load means cover the whole client run, not only its timed window.

| Scene | Before / after FPS | Before / after p95 ms | Prepare ms | Draw ms | KB/frame |
|---|---:|---:|---:|---:|---:|
| Bank | 90.392 / 83.845 | 15.532 / 17.679 | 4.437 / 4.931 | 3.359 / 3.609 | 5.6 / 5.1 |
| Docks | 102.995 / 96.301 | 14.580 / 16.164 | 3.707 / 4.114 | 2.857 / 2.897 | 5.7 / 5.0 |

Bank before/after draws are 2,712/2,516 over 30.0027204/30.0077724 seconds;
docks are 3,090/2,890 over 30.0013170/30.0099988 seconds. Bank main-thread
allocated bytes are 15,429,072/13,259,168; docks 18,001,032/14,944,880.
The allocation fixture isolates the intended 64-byte saving per fresh list;
live allocation includes differing scene activity.

Bank host CPU means are 37.2%/37.2%, peaks 91.9%/88.8%; docks means
42.2%/44.1%, peaks 96.7%/96.8%. Atlas update counts differ: bank 11/5,
docks 9/3. Whole-run load averages do not establish equal contention during
each frame. FPS and tails worsen in both observations. This candidate is a
small allocation reduction; **no CPU-time or FPS improvement is established**.

## Pixel evidence

Candidate and exact-parent same-client plain/batched comparisons match all
pixels in the four world scenes. Cross-launch exact-parent/candidate raw
differences are bank 11,394, forest 285, dungeon 90 and docks 706 pixels.
The forest/dungeon/docks differences reproduce the two terrain variants already
observed in unchanged controls before this patch. Their cause remains unresolved.

The earlier ordinary Release capture before the membership change matches the
candidate at login, forest and dungeon exactly. Bank differs in 5,731 pixels
inside x=2..300, y=1343..1400; docks in 3,695 inside x=2..296, y=1325..1360.
Inspection places these rectangles in live server/system messages; every pixel
outside them matches that earlier capture. This is supporting evidence, not an
exact-parent zero-difference claim. All captures and raw comparisons are retained.
The narrow source change leaves land coordinates, UVs, drawing order and pixel
sampling untouched. Cross-launch deterministic rendering remains an open issue.
