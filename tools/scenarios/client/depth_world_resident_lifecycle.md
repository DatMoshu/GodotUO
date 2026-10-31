# Depth World resident ordering and chunk lifetime

L4 correctness repair, ADR-0044. Existing opt-in Debug Labs --depth-world only.
Canvas, default selection and public exports are not promoted by this change.

## Defects

1. The resident merge globally bucketed texture sets. A,B,A became A,A,B;
   equal-depth cover no longer followed the source primitive order. Bucket
   insertion history also overrode changed chunk traversal. The corrected merge
   retains land/static category passes, preserving within-category order and
   merging only contiguous identical bindings. More draw calls may be necessary;
   this is correctness, not a measured speedup.
2. Resident content depended on MeshLayer.ContentStamp and Count, but direct
   stretched-land writes and pooled/reset layer reuse invalidated only per-quad
   record versions. Same-count terrain could reuse old vertices, texture bindings
   and depth. Direct writes and resets now invalidate both cache mechanisms.

## Regression

BatcherProbe.DepthWorldOrder is called by the existing ordered-world probe in a
Labs build. It inspects the actual resident merged primitive stream used by the
device's category passes: A/B/A, stable reuse, changed traversal, adjacent merge,
hidden/restored group, category order, direct land mutation, pooled same-count
geometry/texture/depth refresh, Reset, and unchanged post-reset zero dirty work.
The direct-write dirty-range assertion starts after Uploaded, so old pending
writes cannot satisfy it. Other ordered-world pixel/recovery tests still run.

Functional validation also follows the live walk that previously produced a
black diamond of missing ground. CPU RenderDump visibility alone is not a pixel
proof: retain screenshots and the exact candidate binding. Keep incomplete
fallback investigations separate from these reproduced defects.

Shared evidence: run20261027_guo-lead_high-fps-polish, resident-order-red-corrected
(9 ordering failures), resident-land-red (6 stale-content failures, order passes),
resident-land-green (renderer suite PASS), followed by exact final-stage checks.
The final fixture additionally clears pending uploads before the direct-write
dirty-range check; earlier red content assertions remain valid.
