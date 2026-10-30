# Edit experimental clouds

This extends the existing experimental overview + speed-lighting cloud plane.
Both existing options must be on. Ordinary zoom and native pixel art are unchanged.
These are layered 2D masks, not volumetric clouds.

Copy one file from `godot/GUO/src/Render/SpeedMode/Weather/Clouds/` to
`clouds.json` beside the active character's `profile.json`:

| Preset | Strategy | Intended appearance |
|---|---|---|
| soft-billowing.json | Hybrid + sprite layer | Broad rounded masses, small upper billows |
| thin-haze.json | Procedural haze | Low-opacity elongated veil |
| broken-storm.json | Broken procedural + hybrid | Dark cover with open gaps |

Edit in any JSON editor; `cloud.schema.json` supplies field completion and
basic validation. The runtime additionally validates duplicate keys, size,
UTF-8, zoom ordering and sprite paths. Safe ranges are in [CONTRACT.md](CONTRACT.md).
Numbers outside the ranges reject the entire file rather than silently clamp.
Leave the cloud range (zoom <=4), then return to reload. Delete `clouds.json`
to restore the previous procedural look. This route requires no AI or plugin.
No attended profile is changed by the probes.

`sprite` is empty for procedural, `builtin:billow` for the bundled original
vector mask, or a PNG/WebP path relative to `clouds.json`. Images must be
<=4 MiB compressed and <=1024x1024. No absolute paths, traversal or directory
links. Transparent margins keep sprite tile boundaries clean. Local assets
must be yours to use; the supplied mask was authored for GUO (BSD-2-Clause).

Wind direction is in world-tile axes; 0 degrees is +X and 90 is +Y. Speed is
tiles/second multiplied by the existing weather wind strength (zero stops it;
negative reverses it). Heights affect visual plane parallax only. Day/night
response consumes the same authoritative resolver result as sun shadows:
validated shard clock first, named ambient-brightness approximation if absent.
Sun versus moon strength and inverse shadow length supply a bounded lighting
proxy, not a second astronomical model or PC clock. The base predates the
local test-clock dependency; the lead integration patch routes its result
through the same mapping when that accepted dependency is integrated.
Native opacity is capped at .82, overview at .32; player pin clearance remains
20..44 screen pixels. Zoom blending never begins below4; no new camera limit.

No Store installation UI is added: cloud component registration does not yet
exist. Shared-hook requirements accompany the evidence packet for the
lead. Presets contain numbers, colors and image references only; no shader
paths or code. No new external dependency, network operation or doctor is
needed. Existing engine/client-data setup is unchanged.

Verification: `tools/scenarios/client/cloud_variants.cs.fixture`. Source/probe
availability does not establish visual acceptance. The final evidence report
records which gates actually ran and any remaining blockers.
