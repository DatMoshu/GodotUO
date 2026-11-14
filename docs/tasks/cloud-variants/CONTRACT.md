# Cloud variants, task 1056

Frozen source proposal, 2026-10-30. Base: `72a7f7db9ddb1c51b376b39ead87e232ac08db73`.
Owner authority: the five-worker visual-corrections handoff. This is a bounded
extension of ADR-0045's experimental cloud plane, not a volumetric renderer.
No shared Profile, UO+ or Store registration edits belong to this worker.

Layer: existing experimental runtime adapter plus L2-style typed data. Existing
overview + speed-lighting opt-ins remain mandatory. Missing/invalid presets
retain the existing procedural plane. Zoom <=4 allocates no cloud resources.
Cloud changes never filter native art, alter picking, or change camera bounds.

## Data contract

`guo/cloud@1` is strict UTF-8 JSON, <=16 KiB, unique keys, no unknown fields,
no executable content. Required root fields: schema, name, zoom_start,
zoom_end, layers. Start is in [4,5], end in (start,6]. One or two layers.
Each layer has algorithm (procedural/sprite-layered/hybrid), mask
(billow/haze/broken), sprite (empty or a relative PNG/WebP file), density
[0,2], coverage [0,1], scale [0.01,0.25] inverse tiles, wind_degrees
[0,360], wind_speed [0,2] tiles/second, opacity [0,1], day_color and
night_color (three [0,1] channels), daynight_response [0,1], height [0,600],
parallax [0,0.03]. Sprite algorithms require a sprite reference; the bundled
original `builtin:billow` vector mask is also allowed. Local sprites must stay
inside the preset directory, without links, and be <=1024 pixels per side.
All algorithms use the same world coordinates, time, pin clearance and caps.

## Usable editing route

Copy one bundled preset beside the active profile's `profile.json` as
`clouds.json`. The next cloud activation loads it once. To reload, leave the
cloud range (zoom <=4), then return. Removing the file restores legacy clouds.
Malformed files log a concise diagnostic and retain legacy clouds; no partially
applied preset. No runtime polling, downloads or new external dependency.

The existing typed Store water contract is the reference. Store has no cloud
component yet. A registration change must be reviewed/applied by the lead;
this packet does not install or execute arbitrary pack content. A separate UI
is deferred because no existing cloud editor seam exists. Manual JSON remains
usable without AI, editor plugins or services.

## Regression and review contract (before build)

Parser probe: all three bundled styles, duplicate/unknown/missing keys,
oversize, invalid UTF-8/schema/type/range, path escape, invalid sprite,
layer count, zero coverage and degenerate zoom bounds. Reject atomically.
Rendered probe: old/default/off exact pixels; each selected strategy at zoom
4/4.5/5/6 and return; day/night, zero/moving wind, world-lock, resize, pin.
Native scene probe must preserve pin/labels and actual 4..6 transition.
Native before/after direct inspection and authorized Gemini review remain
required before a visual acceptance claim. No build/runtime until coordinated
lead admission; no attended-client input or restart.

Lead source review22002 refinement: apply opacity caps and pin clearance after
combined-layer composition; coverage/opacity zero must be exact. Cloud lighting
consumes the same sun resolver result (validated shard clock / named brightness
fallback), never PC time. The accepted local test-clock dependency is absent at
this base; the integration packet supplies the exact lead-owned resolver patch.
Sprite bytes and header dimensions are bounded before decoding. Reload tests
must retire prior layers and compare removed/invalid JSON to exact legacy.

Qualification revision4: native malformed-JSON fixture reproduced an uncaught
InvalidDataException; the atomic legacy fallback explicitly catches it now.
Direct still inspection found repeated sprite lattice shapes; sprite layers now
evaluate a bounded 3x3 neighboring particle set with world-stable jitter,
rotation, independent XY scale and overlap (up to18 sprite reads for two layers).
Synthetic pin clearance is enabled only in overview, matching the adapter.
Retain earlier failed media and requalify the revised source manifest.

Lead22022 amendment supersedes the sun-length proxy refinement above: read
validated ShardClock.MinuteAt(Time.Ticks) directly at a scene-local resolver.
Cycle is0.1+0.9*max(0,sin((minute-360)*pi/720)), normalized1440minutes,
matching sun's06:00/18:00 convention. Missing/invalid clock uses named brightness
approximation. No1048symbols at this base; lead TryMinute/TryBrightness seam later.
JSON wind_speed remains a weather-wind multiplier: either zero stops motion.
Explicit GPU cases cover JSONspeed0/weather1 and positive speed differences;
actual scene cases record clock source/value at06/12/18/00. These amendments
require a new source freeze and coordinated qualification opportunity; old
eec59380 tests do not qualify them. Do not reacquire the handed-off runtime.
