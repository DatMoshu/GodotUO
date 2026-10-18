# JarJar HD lighting

An optional L2 content pack consumed by the L1 **HD lighting (installed pack)**
option. Both Options interfaces default to off. No dependencies, service,
download, installation hook or AI are involved. Native art, geometry, sorting,
picking, UI and filtering retain the Classic contracts. HDR and analytical
height-buffer sampling apply only while this lighting option is active.

Build locally with `python tools/asset_store/hd_lighting_pack.py --out <zip>`.
The default output uses `UO_BUILD_DIR`. The builder hashes every payload and
verifies the ZIP with the normal Store validator. `pack.json` is the source
manifest; the builder adds `files`. The preview is an original synthetic checker.
See [PROVENANCE.txt](PROVENANCE.txt) for JarJar's original commits and authorship.
This does not publish the pack or start a Store server.

Install the reviewed ZIP through the existing Store, select its content
deployment, restart, then enable **HD lighting (installed pack)** in Video.
There is no numeric binding for this component. An installed but unselected
pack does nothing. If there is no mounted lighting component, the option
reports how to activate one and continues with Classic lighting. Disabling
the option releases the extra viewports and restores the selected ordinary
PostFx look without changing its saved state. A black-and-white death frame
temporarily uses the ordinary path and rebuilds HD after revival.

`hd_light.json` is the existing PostFx preset format. The typed `postfx`
component names this JSON and reads its six sibling `hd_*.gdshader` payloads
through verified bounded reads. All six must exist and match their manifest
hashes; there is no fallback to loose files or built-in HD shaders. The
v2 manifest must declare exactly the six fixed shader siblings for each preset;
extra shaders and two components owning the same siblings are refused. Both
installers and the runtime reject shader includes, engine resource paths,
invalid UTF-8 and payloads over 64 KiB. Preset passes use fixed shader names,
never paths. Legacy v1 PostFx packs retain their existing contract. Mount removal
clears its transient look and shader resources; a failed staged mount is disposed.
The shipped four-pass preset fits the mobile cap: sun, GI, bloom, tonemap. Heat and night
vision remain pack assets for alternate authored presets. At most eight
unique HD passes are accepted; exactly one tonemap or night-vision composite must be enabled. Installing
the pack only verifies/extracts data; shaders compile on explicit option use.

The sun hour is a local presentation clock, never an inferred shard clock.
Default noon/manual; optional cycling takes four real minutes per day. Rain,
storm and snow follow existing shard weather. A thunder event supplies the
lightning envelope. Default weather state is dry/clear. Height/occluder/light
budgets and zoom/mobile governors bound rendering work; the full visible-cell
scan still needs a dense-town performance measurement.

## Acceptance fixture

Headless checks: `client.hd.lighting` and the full smoke's
`StoreLightingProbe.tscn`. They verify default/persistence, the option/pack/death
gate, hash tampering/missing shaders, restoring the ordinary look, sun wrap,
weather envelope, tier governor and unlit fixture IDs. The Python regression
compares Classic light collection against base `f7f05bd6` byte for byte after
removing the single guarded delegation. These checks are not rendered parity.

Rendered acceptance needs an authorized windowed/GPU run plus fresh
`godot:runtime`, private shard port and build leases. Never use the shared shard.
Use one isolated client home per label, the same build, 1280x720, zoom 1,
VSync off, one saved private-shard snapshot at map 0 Britain bank (1434,1699,0).
The scenario `client.hd.lighting.dense` launches the existing performance probe
at that exact spot; run with `--no-record` (MovieWriter's fixed FPS invalidates
frame-time measurement), and pass `account`, `profile_root`, `perf_out` and `label` variables (password
via `GUO_SCENARIO_PASSWORD`). It waits for actual sampled frame-time output.

Prepare identical copied test profiles under each label's own `profile_root`;
the scenario sets `profilespath` explicitly. A fresh root can use `default.json`
with the three HD fields before the private GM's first login. For a copied
root, update that character's `profile.json` as well. Never use the owner's
normal profile root. Prepare these labels:

- `classic`: no selected lighting pack; `UseHDLight=false`.
- `off-pack`: selected lighting pack; `UseHDLight=false`.
- `missing-pack`: no selected lighting pack; `UseHDLight=true`.
- `noon`, `night`: selected pack, option on, manual hour 12 or 0.
- `storm`: option on at noon; generate storm weather using the private
  shard's normal weather system and wait for wetness to settle.
- `dense-lamps`: same scene with 100 lit fixtures and 256 wall occluders
  placed on the private shard before saving the common snapshot. Include
  a lit/unlit candle pair, wall sconce and victim-bound lamp splat. Repeat
  with the option off; never modify the shared world for this fixture.

Warm for ten seconds, sample at least 600 frames, repeat each label three
times. Keep probe JSON/logs plus screenshots for noon/night/storm and a paired
off/no-pack reference. Compare frame mean/p95/p99, render CPU, draw calls and
video memory. Record measured GPU times from both the parallel light viewport
and the PostFx stack using Godot's profiler. Report GPU/driver, rendering
backend, resolution, counts and active pack identity. Never report headless
timing as GPU proof. Review off/no-pack images against the same ClassicUO/guoasset
ground truth before claiming pixel parity; weather/mobiles must be frozen for
pixel comparison. A scenario pass records measurements, not a budget verdict.

This R3 work was initially authorized headless only. Rendered acceptance and
dense-town GPU measurements remain required before accepting the feature.
