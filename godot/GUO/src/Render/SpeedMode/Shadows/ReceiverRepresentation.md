# Bounded representation amendment — task1053 / lead22162

Source-only proposal, superseding open representation/calibration prerequisites
in ReceiverContract.md. No shared interface implementation is admitted. Weather
input: task1055 SOURCE-PROPOSAL.md, weather-containment run, read2026-10-30.
All new code remains unwired and unexecuted pending the existing build queue.

## Concrete storage and admission

`ReceiverStructureSnapshot` owns fixed arrays for a region of at most32x32 tiles.
Each tile holds at most4 `ReceiverPhysicalLayer` records (owner64, native Z32,
kind32),8 `ReceiverCover` boxes (six floats), layer/cover counts and state byte.
State0=not enumerated,1=known complete,2=rejected. Empty known-complete cells
remain distinct from unknown cells. No tile publishes truncated layers/covers.
Layer IDs are nonzero, canonical slot IDs (`cell*4+slot+1`) scoped to the exact
frame key; identity across tickets is physical owner+Z+kind, never a reused slot.

The key includes scene epoch, frame generation, physical topology generation,
surface origin, target dimensions and all six view-transform components. Values
come from the existing admitted core ticket; no independent callback counter or
approximate view comparison. `Seal` ends mutation permanently; consumers reject
unsealed/mismatched keys. No reset/reuse API can mutate borrowed arrays. Pooling
would require a separately reviewed retirement-owned reset; this proposal has
none. Queued callbacks retain the owning snapshot until accepted72a7 retirement.

Per-cell production has an explicit `ReceiverEnumeration` receipt: start/end
topology, visited/expected native node counts, unsupported flag. Admission checks
equal generations/counts, <=64 visited nodes, <=4 layers, <=8 covers, finite
bounded native Z and cell-clipped XY. Duplicate cell writes permanently reject
that cell. Values are copied from input spans; later mutations cannot alter them.
Malformed sorted layers, duplicate Z planes, missing owners, unsupported receiver
kind or out-of-cell structural bounds reject. Cover overflow cannot mark a tile
exposed. `TryLayer` additionally requires matching native owner and receiver Z.

## Exact producer algorithm for the shared scene seam (not applied)

1. On the game thread, capture the admitted scene/frame key and physical topology
   version. Select an integral region <=32x32 in the already loaded map. Do not
   load missing chunks merely for this experiment. Unloaded/missing cells remain0.
2. For each cell, capture the complete native tile-chain membership/count under
   that version. Include physical Static/Multi/Item structures regardless of
   AllowedToDraw, camera maxZ, roof cutaway or color draw admission. Capture land
   corners and relevant neighboring structural footprints. Enumerate at most64
   nodes; if there is a65th, use a mismatched count/unsupported receipt. Do not
   finish an unlimited scan simply to count it. A counter of65 is enough to fail.
3. Classify immutable source descriptors: flat land or known horizontal thin
   surface -> layer at actual top Z; flat roof -> roof layer plus structural
   slab; upper thin floor -> floor layer plus slab; reviewed axis wall faces ->
   vertical structural boxes/planes. Use actual world bounds, split any known
   overhang across all intersected cells, and include zero-thickness structural
   planes. Nonstructural decor contributes no cover. Unsupported slopes, corner
   geometry, ambiguous top surfaces/overhangs or unknown structural semantics
   set Unsupported. A flags-only guess never certifies a missing footprint.
4. Merge only exact duplicate physical planes with a stable canonical native
   owner witness; otherwise equal-Z ambiguities reject. Sort layers by increasing
   Z and freeze owner+kind. Clip structural boxes to this tile while retaining
   their complete vertical interval. Every affected cell must be invalidated
   when a structure footprint changes; moving items use old AND new footprints.
5. Capture end topology and expected membership count, call PutCell with the
   receipt and bounded spans, then Seal. If topology changed during production,
   affected cells reject (key mismatch rejects publication if the core advanced).
   Native color/picking draw order and existing surface XYZ remain unchanged.

The implementation here is the bounded receipt/storage/query model, not a
GameObject reader. Shared source enumeration and topology invalidation remain
lead-owned implementation, with the algorithm above as its reviewable contract.
Weather1055 and shadows consume these same records, not separate roof tests.

## Complete corridor and bounded query cost

Native structural domain is Z[-128,382] (sbyte Z plus byte tile height); extended
values reject. SkyZ512 is strictly above that domain. This is an explicit domain
bound, not a guessed highest roof in the visible neighborhood.

`TryCopyCorridor` enumerates the conservative XY rectangle covering the entire
receiver -> source segment, including adjacent tiles touched at exact integer
edges. It requires every cell to be complete, caps the rectangle at64 cells,
and checks caller output capacity BEFORE copying. At most512 cover records are
copied. Region exit, missing cells, long/diagonal rays or any rejected cell return
unknown with count0; they never shorten the ray. For sun the endpoint is SkyZ512;
for point lights it is the real finite source, so structures beyond a lamp do
not block it. Low-angle sun can exceed the corridor/region bound and be rejected;
this limitation must be visible in admission diagnostics, not mistaken for a fix.

Bound wrappers derive the corridor from the actual source/projected sample:
`TryProject` resolves a matching physical layer, computes the candidate, gathers
that exact ray, then clips against its structures. `TrySheltered` gathers the
vertical contact -> SkyZ corridor. A caller cannot reuse a short ray's complete bit
for another ray through these wrappers. False means unknown, NOT exposed.
Weather must honor the success result separately from the sheltered output.
The forward projection remains an oracle for inverse per-pixel mask mapping;
integration must compare the candidate to the actual published receiverXYZ.

Storage payload per snapshot:1024*(4*16+8*24+3)=265216 bytes (~259KiB), plus
array headers/key/object. Two retained tickets cost~518KiB payload. Producer
work <=65536 native-node visits, <=4096 layers and8192 covers per region. Each
ray examines <=64 states twice, copies <=512 covers, then <=512 slab tests.
Scratch is caller-owned <=12288 bytes and must be reused, not allocated per
pixel. These are hard operation bounds, NOT timings or a performance approval.
Running the reference CPU collector per rendered pixel is not proposed shipping
wiring. A future RD layout/index must preserve these gates and be cost-reviewed
before implementation; overflow rejects optional output rather than raising caps.

## Native billboard calibration is now numeric

`ReceiverNativeFrame` contains the actual pre-view affine corners and sampled
texel centres plus same-frame opaque bounds/contact metadata from1052. Token is
the unique owner/draw/frame occurrence in the admitted ticket, never Graphic.
`TryCalibrateFrame` maps cached opaque texels through that exact native draw.
It currently accepts upright axis-aligned affine frames, including horizontal
UV flip. Sitting/sheared/deformed samples reject until their explicit mapping
is reviewed. No tiledata-height fallback, alternate frame lookup or alpha readback.

For a unit tile-space horizontal axis, native projection length is22*sqrt(2).
Therefore native horizontal pixel offset u maps to u/(22*sqrt(2)) tiles along
the source-oriented horizontal perpendicular. Native vertical offset v above
the lowest opaque contact maps to v/4 UO-Z. BaseZ is the supplied physical world
contact Z; topZ=baseZ+(contactNativeY-topOpaqueNativeY)/4. E.g.80 actual native
pixels above contact ->20 UO-Z even for a zero-height foliage tiledata entry.
`TryMap` verifies the native occurrence token and canonical calibration, then
maps each opaque sample. Contact maps exactly to base; UV flip retains height.
Point-light billboard orientation is frozen relative to source -> base; the actual
per-sample source ray still uses source -> sample. Vertical/coincident horizontal
source has no orientation in this model and rejects billboard mapping.

This explicit isotropic-width/source-oriented billboard is a 2.5D approximation;
it will need visual calibration/acceptance. It does not promise identical length
to the previous screen-skew heuristic, nor measured tree heights. No opacity or
new tuning control hides a geometric mismatch.

`TrySunSource` also freezes the unit conversion: for existing native skew length
L (horizontal native pixels per vertical native pixel), unit horizontal world
direction toward the source is the negative shadow direction, with
Dz=(22*sqrt(2))/(4*L). This exposes a practical admission limitation: L=1 at
ground Z0 needs~65.8 horizontal tiles to reach SkyZ512, exceeding a32-tile region.
Thus the current32/64-cell proposal can qualify local lamps, vertical shelter
and steeper synthetic sun corridors, but **cannot qualify ordinary current sun
lengths against that global sky bound**. Do not silently shorten those rays.
Before shared sun wiring, lead must choose a larger bounded capture/index or an
authoritative global structural-height/occupancy bound. This is an actionable
integration blocker revealed by the calibrated units, not a request to widen
this source-only assignment. A local maximum observed roof is insufficient.

## Frozen regression additions

ReceiverStructureProbe covers copied data, four stacked layers, unknown versus
exposed cells, wrong owner/Z, frame/scene/topology/origin/resize/view mismatch,
duplicate/overflow/incomplete cells, immutable sealed storage, full versus
over-budget corridors, no partial copy on failure, same-record weather shelter,
roof-top exposure, affine/flip calibration, contact/top numeric oracles, native
frame mismatch and bound positive/negative shadow queries. These are source
regressions, NOT compiled/executed results or native producer proof. Existing
ReceiverGeometryProbe remains and no accepted1041/1037 rerun is requested.

Lead22188 amendment: the same ReceiverGeometryProbe now embeds weather's exact
V01-V20 table, exact X4 wall/closed corner/tangent versus nearby paving, own
roof/wall origin, invalid line/point and late-invalid-cover outfalse controls.
TrySheltered publishes its sheltered result only after all cover validation,
so late invalid metadata also returns failure/outfalse. ReceiverStructureProbe
adds four-touching-cell gather, missing-fourth-cell rejection without partial
copy, snapshot/corridor outside unknown, neighbour-owned zero-thickness wall
face/corner and nearby exposed controls. No guessed thickness or overhang.
All assertions remain source-only, uncompiled/unexecuted before queue admission.
