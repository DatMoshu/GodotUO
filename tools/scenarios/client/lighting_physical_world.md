# Britain physical lighting (ADR-0049)

This is an offline Debug fixture, using the configured native client installation.
It does not connect to a shard or use an owner's account, settings or save.
The enhanced option stays off by default.

1. Build `godot/GUO/GUO.csproj` with the pinned .NET SDK.
2. Run `godot-console --headless --path godot/GUO res://src/Render/SpeedMode/PhysicalWorld/PhysicalWorldProbe.tscn`.
   Require `[physical-world] PASS`. This checks source contracts and analytic
   rays only; it does not establish rendered behavior.
3. Run `StructuralLampProbe.tscn`, `StructuralShelterProbe.tscn`,
   `ReceiverSoftenProbe.tscn`, and `LightPresentationProbe.tscn` from the same scene directory, with
   `--rendering-method mobile --resolution 256x256` and a separate
   `GUO_PHYSICAL_WORLD_OUT` evidence directory for each. These require a real
   RenderingDevice, not `--headless`. Require exit zero and each PASS line.
   They compare real GPU depth/status/compositing results with independent
   geometry and transfer expectations, including resource retirement.
   The soften fixture requires positive smoothing on a continuous plane and no
   transfer across physical height, position, confidence or class boundaries.
4. Set `GUO_PHYSICAL_WORLD_OUT` to a new directory inside the external evidence
   run. Run the following with the pinned `godot-console` and configured client
   data (replace `EVIDENCE` with that same absolute directory):

   ```text
   --path godot/GUO --rendering-method mobile --resolution 1280x720
   res://src/Render/SpeedMode/PhysicalWorld/NativePhysicalWorldProbe.tscn
   -- --no-focus --no-splash --silent --pregame-classic
   --cache-dir EVIDENCE/data/fixture/cache
   ```

5. Require process exit zero and `[native-physical-world] PASS`. Inspect
   `data/native-physical-world-results.json` and the actual native captures.
   The fixture places native ground Items over Britain at 1417,1734; it exercises
   the production live-overlay producer and actual surface/HDR/depth targets.
6. Verify structural bypass/on attribution, a positive wall-to-ground shadow,
   upper-floor negative and positive, native roof receiver, cutaway-independent
   depth/shelter, and first published generation after roof removal or door
   movement. The selected lamp's closed/open door comparison must change both
   raw light and the actual displayed image. One/three lamp overlap must retain
   raw HDR while preserving native paving detail in the displayed result.
7. Exact parent-off and injected-failure bytes must match in both the native
   world target and final viewport. Inspect the `*-presented.png` images: the
   ordinary world-target captures precede lighting composition and alone do
   not establish what the player sees. A disabled pass cannot pass a positive
   output assertion.

Unknown ray corridors remain unknown. A qualified depth hit can prove a shadow;
a miss in incomplete source coverage cannot prove clear sky. All roof shapes,
all selected point lights, wind-driven precipitation, low-angle full-world
coverage and performance acceptance are separate requirements. The shelter
fixture verifies the status texture consumed by weather; it does not establish
the appearance of falling rain. To add emitted-weather acceptance, set
`GUO_PHYSICAL_EMITTED_WEATHER=1` on the native fixture command. This adds real
rain packets, a hidden native Item canopy, same-ticket rain/wet shader checks,
exposed positive controls, unknown fallback, first-frame roof removal and
Classic/failure comparisons with a positive native-precipitation witness. Both
the world target and final viewport must pass. The separate
`Weather/SpeedModeWeatherRuntimeProbe.tscn` also verifies that approaching-storm
lines remain visible when the procedural pass emits no rain.

Debug correctness runs do not establish Release
frame times or justify whole-world visibility bakes.

For a bounded CPU/memory diagnostic, set `GUO_PHYSICAL_PERF=1` and use a new
`GUO_PHYSICAL_WORLD_OUT` directory with the same native scene command. This
selects the performance fixture instead of the image acceptance cases. Require
`[native-physical-performance] PASS` and inspect
`data/native-physical-performance.json`. Clear the variable before running the
normal image acceptance cases again.

The diagnostic freezes the scene, clock and camera, warms 30 draws and samples
120 draws per mode, Classic then enhanced. It records direct `DrawFrame` CPU
submission separately from paced wall intervals; readbacks and GPU/memory
queries stay outside those samples. The mode comparison includes all enabled
enhanced lighting work, not just the new physical passes. A separate no-draw
cache diagnostic measures full `Capture` calls, including signatures and
snapshot sealing. Fresh cache means a new physical cache with already warm
native file/art caches, not cold disk loading.

Memory counters describe the process and engine allocations, not total adapter
residency. GPU marker observations are a separate, delayed diagnostic window;
cached depth work may have no marker and the existing markers do not cover all
lighting work. On the pinned Vulkan engine, GPU timestamps are nanoseconds and
CPU timestamps are microseconds, despite the SDK XML describing both as
microseconds. The pinned implementation is authoritative. These Debug samples
under shared load establish neither Release performance nor uncapped FPS.

For appearance-neutral sky/direct bookkeeping, run the headless
`res://src/Render/SpeedMode/SkyLightTermsProbe.tscn` and require its PASS line.
Then run `SkyLightTermsGpuProbe.tscn` in the physical-world scene directory with
the real RenderingDevice and a new external evidence directory. It compares
the original ambient expression with the current full light shader, requiring
bit-exact normal HDR, independent sky/direct numeric expectations and unchanged
lamp-only HDR. Its receivers and depth inputs are synthetic, not native art.

Finally set `GUO_PHYSICAL_SKY_TERMS=1` for the native command above, using a new
output directory with the performance/weather variables cleared. This selects
a separate native suite for noon, night and missing-clock moon fallback.
Require the native PASS line, positive terms in the actual displayed frame,
unchanged lamp contribution after removing one real fixture lamp on pixels with
identical measured sun-mask bytes, exact stable normal restoration, and exact
disabled/failure output. Lamp removal also removes its sprite shadow; report
that independently excluded domain. The native restoration compares
two current-code captures; the GPU suite supplies original-expression parity.
Clear the sky variable before normal physical-world acceptance.

Setup and term0-only re-enable captures are retained as separate observations.
The first displayed frame after re-enabling currently differs from the settled
frame despite identical HDR/world targets. To reproduce with the original
ambient expression, add `GUO_PHYSICAL_SKY_TRANSITION_LEGACY=1` in a fresh process
alongside the sky variable. This control does not run diagnostic term outputs.
Stable sky-term acceptance does not claim that presentation transition fixed.

These terms retain the current artistic shadow strength, roof factor and night
floor. They are prerequisites for later local sky-visibility work, not a claim
of physical irradiance, ambient occlusion or global illumination. Debug term
selection is fixture-only and does not add a player setting.
