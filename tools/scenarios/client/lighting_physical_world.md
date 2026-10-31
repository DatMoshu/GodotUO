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
the appearance of falling rain. Debug correctness runs do not establish Release
frame times or justify whole-world visibility bakes.
