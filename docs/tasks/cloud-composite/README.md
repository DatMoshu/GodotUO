# Foreground cloud composition (task1057)

Layer: correction to the existing experimental overview and Lighting speed
mode opt-ins. Neither is enabled by this change. No new setting or public API.

The cloud plane renders into an independent transparent target at the current
world dimensions. Final ordering is the lit/postprocessed world, including its
existing fog/weather/reflections, then clouds, then UI. Overview labels retain
their world stage; existing pin mask and overview opacity cap are unchanged.
Cloud pixels no longer enter the ground light or postFX target.

RenderTargets borrows a publication for one controller draw frame. It binds
scene owner, frame serial, world identity/dimensions, overlay identity and
generation. Frame entry/resize/abort/disposal clear it; duplicate publication
rejects that frame's optional output. Departed owner/generation releases cannot
clear a new scene. Scene release revokes publication and pooled native/retained
draw references before freeing its target/material/items. No render callback
reads a live map or profile. Native textures and the final cloud sample remain
nearest; final cloud textures use premultiplied AlphaBlend.

## Standalone checks

Use the pinned `godot-console` with ordinary project leases, real Mobile or
Forward+ rendering at1280x720, external evidence output and an isolated cache.
Run `dotnet build godot/GUO/GUO.csproj` first. No account or shard is needed.

- Set `GUO_CLOUD_COMPOSITE_OUT` to an external run directory. Run
  `godot-console --path godot/GUO --rendering-method mobile --resolution 1280x720
  res://src/Render/SpeedMode/Weather/CloudCompositeContractProbe.tscn`.
  Require `[cloud-contract] PASS`: actual transparent viewport alpha0/.5/1
  readback, wrong-blend negative control, clear/off/abort, native and retained
  pooled commands, queued unload, new owner and resize. This uses original
  solid colors and does not prove native shadow-source attribution.
- With the same output variable, set `GUO_CLOUD_NATIVE_LIFETIME=1`. Run the
  `NativeCloudCompositeProbe.tscn` scene in the same directory, passing explicit
  `-- --data-dir <installation> --client-version <version> --cache-dir
  <output>/data/fixture/cache`. It creates its own settings/workspace/profile,
  refuses login arguments and never connects. It captures world, classic light
  and FINAL root viewport across4/4.25/4.5/5/6 in both directions, and a clearly
  labelled synthetic final-light stripe. Additional checks cover final pin/label,
  pan/time, parent-off, abort/early return in retained and ordinary modes,
  queued scene unload, old-owner release after new scene and resize.
- Run `launchers/dev/smoke.bat` before any commit, with the normal resolved
  client-data environment and external `UO_BUILD_DIR`.

Preserve source manifests and DLL hashes before baseline and candidate runs.
Never interpret the synthetic stripe as a sun/lamp/window source. Separate
native sun/static/mobile/lamp/window/wet/reflection attribution and integrated
owner gameplay remain review gates. This correction does not select a cloud
preset or establish polished appearance, performance or warning-free shutdown.

Scenario: `tools/scenarios/client/cloud_composite.md`.
