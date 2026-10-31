# Receiver/physical-cover proposal — task 1053

Status: **pure helpers compiled; GPU inverse mask still unwired. Not an accepted ADR**.
Amendment: lead22162 found the original geometry domain coherent. Concrete
storage, corridor receipts, bounds/cost and numeric native calibration now live
in ReceiverRepresentation.md and the additional pure Receiver* source files.
Base: `72a7f7db9ddb1c51b376b39ead87e232ac08db73`. The 2026-10-31 integration
compiles those helpers and runs them from `ReceiverHeightProbe`. It also clips
an upright sun silhouette to the span above an active cover, and it stops
treating hidden roofs as permission for an indoor caster to stamp. That clip
is opt-in with sun shadows. The per-pixel inverse mask, roof-plane receivers,
and a sun corridor that can reach SkyZ 512 are still not wired. No new option
or presenter. Layer: correction to the existing opt-in ADR-0048 renderer.

## Problem and supported domain

`SpeedShadowInputs.Stamp` currently drops caster world height before
`ProjectedShadowMaskRd` performs R8 MAX accumulation. `SurfaceRow.For` labels
thin elevated floors kind1 as well as ground. A final kind1 test cannot identify
which caster belongs on a receiver. A MAX-merged caster-Z channel cannot recover
the missing association when casters overlap. Static admission also uses visual
`_maxZ` and `_noDrawRoofs` in a physical-cover decision.

First supported domain: a known horizontal floor or flat roof patch, known
world XYZ, native opaque silhouette sample, descending source ray and complete
structural-cover corridor. Ground, upper decks, elevated casters to lower ground
and tall casters to higher roofs are supported by the analytic reference.
Slopes, wall receivers, arbitrary stacked geometry lacking complete metadata,
horizontal/upward lamp rays and unknown layer/surface samples reject optional
projection. Do not reinterpret a slope as a horizontal floor. No globally
leak-free, gameplay-fixed or volumetric claim follows.

## Immutable geometry contract

* Units: X/Y in world tiles, Z in native UO elevation units. Native screen
  projection uses `(22*(x-y),22*(x+y)-4*z)` before camera translation. Directions
  must use this same mixed tile/tile/UO-Z basis. Never normalize a tile-space
  horizontal direction and then treat native pixel length as world Z.
* Native-contact lane1052 supplies the actual sampled art/frame, UVs, affine
  corners, opaque contact and same-frame owner identity for transient/resident,
  replaced/themed/animated art. This lane neither reconstructs anchors nor
  fetches `obj.Graphic` as a substitute. No per-frame texture readback.
* Scene snapshot supplies baseZ/topZ and an explicit mapping from each opaque
  native alpha sample into a bounded 2.5D silhouette model. The helper accepts
  the resulting world sample, not an entire stamp or tiledata height alone.
  A sample must lie within that span. In particular zero-height foliage cannot
  silently become a zero-height physical tree. Contact pixels are not baseZ.
* Proposed initial mapping: source-oriented vertical billboard rooted at the
  supplied contact worldXY/baseZ. Its horizontal axis is perpendicular to the
  source's horizontal ray, in tile XY; its width and vertical scale are explicit
  frozen fields. Native sample coordinates map through those axes. The same
  mapping must drive mask raster bounds and inverse alpha lookup. This is a
  documented silhouette approximation, not reconstructed mesh geometry.
  ReceiverBillboard now implements explicit native-width and topZ calibration
  from the captured frame's affine geometry and opaque metadata. Its supported
  upright domain and approximation are specified in ReceiverRepresentation.md.
* Receiver snapshot supplies actual surface worldXYZ and nonzero physical
  layer ID. The reference patch has half-open XY bounds and one horizontal Z;
  layers distinguish planes but need not equal the caster's layer. Exposed roof
  top and covered floor below are distinct receivers. A zero/default layer is
  unknown. Existing kind1 alone supplies neither boundedness nor layer identity.
* Structural cover is an immutable span of closed world boxes/planes. Include
  all intersecting roofs, upper slabs and walls in the receiver-to-source
  corridor, including hidden ones. Zero-thickness roof planes are supported.
  Boxes do not include the silhouette caster itself. Roof top at the ray origin
  is not overhead shelter; structural volume above the origin is. Tangent
  boundary contacts conservatively block. Sloping roofs must be represented by
  reviewed conservative bounds or rejected, never guessed to be exposed.
* `completeCover=false` rejects projection and returns unknown shelter.
  `true` with an empty span explicitly means known exposed space. For sun,
  SkyZ must be above every relevant structural cover; scene capture must prove
  that bound and corridor membership. A clipped chunk scan is not completeness.
  For a lamp, capture only the finite receiver-to-lamp corridor. Cover behind
  that source does not block it. Bounded cover overflow rejects affected work.

No live GameObjects, map/chunk lists, profile reads or texture ownership changes
are introduced in the pure helpers. Integration must copy/freeze borrowed spans
into ticket-owned storage before callbacks; a ReadOnlySpan is not itself an
ownership guarantee.

## Geometry and clipping order

For opaque sample Q and candidate receiver elevation Rz, let D point from Q
toward the point light, or toward the sun. Require Dz > 0 and Qz >= Rz.
Then `t=(Qz-Rz)/Dz`, `R=Q-t*D`. Accept only a finite R inside that receiver
patch. For point light S, `D=S-Q`; for sun D uses the mixed world basis.

Test the entire open segment R -> S against structural cover. For sun S is the
intersection of the same ray with the proven SkyZ plane. Any structural
intersection rejects this caster's optional direct-light contribution. This
checks cover both between receiver/caster and between caster/source. An indoor
prop cannot cast exterior sunlight merely because its own screen shadow falls
outside a roof. A tall tree sample above a roof may shade that roof's top;
its low sample cannot. Native silhouette alpha is tested separately using
the exact art/frame snapshot. The geometry helper does not replace alpha.

Per receiver pixel, inverse-map the source ray into the frozen billboard,
obtain its native alpha sample, and apply this geometric/cover test **before**
R8 MAX. Forward helper projections serve as analytic oracles for that inverse
mapping. Multiple casters retain independent span/model/cover association until
their clipped contributions merge. Unknown samples write zero; no final-mask
blur or roof-wide exclusion repairs missing geometry.

Weather uses `TrySheltered`'s vertical point -> sky interpretation with the same
physical structure snapshot. Sun uses its angled corridor; local lamps use
finite corridors. These are distinct queries over shared physical facts. A
single lowest coverZ or one covered bit is insufficient for stacked interiors.
Cutaway changes neither the physical snapshot nor either query.

## Exact shared integration changes requested (not applied)

1. `GameScene.SpeedModeShadows.cs`, static covered-prop admission: replace the
   `_maxZ` / `_noDrawRoofs` exception with physical snapshot eligibility.
   Do not simply reject every caster whose base is below the lowest cover:
   tall casters can extend above it. Native geometry comes from1052; attach
   span/model/owner/source and a ticket generation, not another art lookup.
   `GameScene.SpeedMobileShadows.cs` needs the same metadata per native group.
2. `SpeedShadowInputs.cs`, Stamp and Draw: retain that metadata per stamp;
   capture immutable cover ranges and receiver-ticket identity. Extend the
   mask call with the surface resource, surface origin/target/view generation,
   per-stamp geometry and corridor completeness. Validate all identities before
   rendering. This cannot be a one-line extra merged height texture.
3. `ProjectedShadowMaskRd.cs`, Stamp/pipeline fragment: bind the exact published
   surface, reconstruct that pixel's worldXYZ, inverse-map to the source-oriented
   billboard, test actual alpha/span/receiver/cover, then MAX the accepted alpha.
   Current fragment has only sprite alpha. Proposed extra binding data must
   remain per stamp; polar wall atlas and ordered color/picking stay unchanged.
4. Surface producer (`SurfaceRow.cs`, surface vertex/fragment publication and
   ticket): supply stable physical layer/patch association and authoritative
   cover-range lookup. Preserve kind/normal semantics and camera-relative origin.
   The bounded source representation is proposed in ReceiverRepresentation.md;
   no packed-bit layout or RD buffer binding is applied or approved here.
5. `light_fragment.glsl.txt`: consume only a matching clipped mask. Keep unknown
   or unsupported receiver contributions zero. A valid flat-roof contribution
   needs an explicit reviewed receiver-kind change; do not silently turn all
   kind5 pixels into ground. Retain classic fallback at transaction failure;
   no legacy suppression by enabled setting alone (attribution1054 owns that).

The helpers alone cannot fix the current rendered mask: surface/cover ownership,
billboard calibration, inverse raster mapping and callback lifetime remain lead
integration decisions. Reject a stale/mismatched surface ticket before optional
publication, preserving the accepted frame failure transaction and queued-work
retirement. No post-hoc classic suppression or partial fallback rule is added.

## Frozen regressions and qualification

`ReceiverGeometryProbe.Run()` is an unwired pure C# probe. Analytic expected
coordinates (e.g. 20Z / 10Z-per-tile = 2 tiles) cover exposed floor, upper deck,
elevated caster -> lower ground, tall tree -> higher roof, forbidden short/low sample,
indoor caster, roof crossing, cutaway invariant, roof top, porch/exposed paving,
wall crossing, point lamp/opposite side, horizontal/upward rejection, overlapping
caster spans, finite/half-open bounds and missing/invalid metadata. The prior
screen-overlap/kind1-only policy would admit designated forbidden cases; the
blanket receiverZ<=baseZ policy would reject the tall-tree positive case.
The cutaway case proves the pure query has no visual state; native on/off
captures remain required to prove the producer preserves physical metadata.

The 2026-10-31 integration compiled these helpers and ran
`ReceiverGeometryProbe.Run` (66 checks), `ReceiverStructureProbe.Run` (64) and,
in a Debug build, `ReceiverReferenceProbe.Run`. `ReceiverHeightProbe.tscn` is
the headless wrapper. That run is not a rendered mask, a bank capture, or a
native art proof. Shared GPU inverse mapping still needs reviewed admission.
Native proof still needs forbidden-region zero output and positive legal output,
with direct inspection on a Windows client. Accepted 1041/1037 are not rerun
by this change.
