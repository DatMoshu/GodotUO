# Lighting speed shadows (ADR-0048 amendment)

`GameScene.SpeedModeShadows` snapshots loaded wall sources, sun casters,
per-lamp mobile body silhouettes and covered/open window boundaries after
the core reach walk. `SpeedShadowInputs` produces real GPU inputs through
`SpeedLightFrame.RenderInputs`, then adds windows through `RenderPatches`.
The core calls `ReleaseInputs` on off, failure, resize and shutdown.
The lead owns the shared scene call, metadata resource and option wiring.
Classic/off does not construct the adapter. Headless fixtures do not prove
visual parity; the rendered `ShadowHookProbe` checks the real callback,
alpha, selected-lamp term, disabled restoration and resource release.

The current scene adapter samples native mobile body frames only; equipment,
mount and sitting silhouette parity require capture from the existing native
draw, rather than reimplementing its animation selection. The optional clock
argument takes the validated shard stamp. Until it is supplied the source is
explicitly `shard-brightness-approximation`. Window emission is conservative:
both a classified wall face and a covered-inside/open-outside pair are required.

`WallShadowGrid` freezes the existing blocker walk's eligible solids and the
same `RealtimeWallFaces` edge lines. Call `AddBlocker` while source flags are
still available, and `AddEdge` for each wall face. Do not copy the reach field's
aggregate BlockTop: its maximum may include a tree. Do not turn a wall edge into
a solid tile. Trees and mobiles never enter this grid. Two RGBA32F array layers
retain signed UO heights and fractional wall offsets without quantization.
`AddBlocker` requires the source tiledata name and whether another piece on
that tile has Foliage: bare trunks are rejected by name or neighbouring foliage
even when they only carry Impassable. The probe names each rejection rule.
The grid is camera-relative and bounded to 512 by 512 tiles. Stable snapshot
revisions cost O(1); rebuilt snapshots compare bytes and upload only on change.
The game thread prepares it after waiting for the previous
render transaction; it must remain frozen throughout the next transaction.

`LampShadowFrame` selects the nearest 0..10 offers (default 6) and maps each
source offer to its current atlas row. `PolarShadowMapRd` performs one compute
dispatch, one 256-thread workgroup per lamp. DDA rays intersect the actual
edge planes or solid tiles out to the lamp radius. The 256 x 64 RG16F atlas
contains horizontal blocker distance in tiles and wall-top Z in UO units.
Unselected or failed rows are never exposed. No CPU light field or shadow
mask is computed. The synthetic probe's segment-intersection oracle is
diagnostic only, independently written from the shader's DDA traversal.

`ShadowSampling.Glsl` interpolates visibility only along the angular axis;
the light index remains nearest. It offsets the receiver along its exposed
normal, applies the 0.2-tile distance bias and configured softness, tests the
ray's UO height at the blocker, and keeps 15% of a shadowed lamp. Reconstruct
surface XY with the frame's integer camera origin before taking light-relative
coordinates. The atlas records the nearest wall only; overlapping walls at
different heights are an approximation, not a multilayer occlusion solution.

`MobileLampProjection` selects at most the two nearest affecting lamps per
mobile, supplies a bounded screen-space skew and fades by lamp distance.
The scene adapter must stamp native silhouettes at the interpolated contact
point, separately per lamp; do not apply their mask to unrelated light terms.
This selection contract does not yet prove live silhouette gathering.

The sun mask is transplanted from `lighting/perf-20261029` at `bbd51955`:
native alpha stamps combine with MAX, never additive opacity. Its CPU twin
is absent. The batcher retains the existing view transform for full camera
coverage at every zoom. Props use their lowest opaque row; mobiles use
interpolated offsets; Bridge, roofs, floors and lying items are excluded.
The light pass consumes the mask on floor ambient only.

`World.ShardClock` retains validated packet 0x5B time and advances it by elapsed
client monotonic ticks. Logout invalidates the stamp. Missing time uses the
parked sprint's explicitly labelled `shard-brightness-approximation`, never PC
wall time. The day/moon arc is a simple lighting model, not astronomy.

All RD methods run on the render thread with borrowed texture owners alive.
Resource or format failures disable that optional result without stale rows.
Dispose passes on that thread and their `Texture2Drd` wrapper on its owner.
The product passes never read pixels back.

Headless scenes: `LampShadowProbe.tscn`, `SunShadowProbe.tscn`. In an assigned
Godot slot, run windowed at 1280x720 with `-- --gpu` for GPU fixtures; set
`GUO_SHADOW_PROBE_OUT` to the evidence `stills/` directory. Fixtures test wall
span/distance/height, translated coordinates, two-lamp selection, full-view
sun stamping and MAX overlap. Bank/shop captures and off parity remain live
integration acceptance checks.
