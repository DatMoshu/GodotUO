# Lighting speed shadows and window light (ADR-0048)

Layer L1; existing `LightingSpeedMode` is off on new and old profiles.
No shard, account, local clock, artwork generation or performance sampling is
needed for the standalone contracts below. Save logs and PNGs to external evidence.
Use the project's pinned `godot-console`, taking a GUO runtime lease for GPU work.

1. Build the client and run full smoke. Run `godot-console --headless --path
   godot/GUO res://src/Render/SpeedMode/Shadows/SunShadowProbe.tscn`. Expect
   `[sun-shadow-probe] PASS`: profile persistence/clamps, contact rules and
   monotonic clock advance/wrap. Parent remains off; lamp count defaults6 and
   clamps0..10; sun/window children default true but remain inert under off.
2. Run the headless scene `res://src/Render/SpeedMode/Shadows/ShardClockProbe.tscn`.
   Expect `[shard-clock-probe] PASS`: actual packet0x5B remains four bytes,
   fragmented input waits, invalid clock fields clear the stamp without consuming
   the next packet, and `World.Clear` invalidates it. The socket never connects.
3. Run `LampShadowProbe.tscn` and `SunShadowProbe.tscn` in the same Shadows
   directory with a rendered Mobile/Forward+ window and `-- --gpu`. Expect
   wall distance/top/span and translated-origin equality; trees and bridges
   excluded; sun coverage reaches the full viewport; duplicate alpha uses MAX.
4. Run `ShadowHookProbe.tscn` rendered. Expect real callback inputs, native-alpha
   selected-lamp attenuation, disabled-target byte equality and exactly one
   release on off. It uses synthetic native alpha, not a bank/mobile comparison.
5. After the lead binds the window JSON resource, run the headless
   `res://src/Render/SpeedMode/WindowLight/WindowLightProbe.tscn` and
   `WindowLightPassProbe.tscn`; then run the latter rendered with `-- --gpu`.
   Expect60 metadata entries, pane geometry, translated-origin restoration and
   rejection of uncovered/high/wrong-class surfaces. Set `GUO_SHADOW_PROBE_OUT`
   to the external stills folder for GPU PNGs.
6. Open UO+ Options. Parent off or unavailable disables all children. Stage
   lamp0/10, sun and window changes; Cancel preserves the profile; Apply stores
   them. Defaults stages lamp6/sun+window on and parent off until Apply.
   Night floor and Soften remain available inside the same parent guard.

Integrated acceptance, after the shared scene call is applied: at1280x720,
capture night bank lamp shadows and day shop window panes. Include sun/native
mobile body contacts, at most two affecting lamps per mobile, missing-clock
brightness approximation, and valid server-clock direction. Current collection
uses copied completed native body-pass quads, including equipment, mounts,
sitting deformation and flip/offset geometry. Isolated native fixtures cover
the exercised human/robe/sword/horse/chair combination, not every art or race.
Compare Classic off-before/off-after images and draw counts; inject light and
world failure and check recovery. Repeat off/on/off, resize and logout to check
resource lifetimes. No headless result substitutes for those captures or quiet
performance acceptance. Preserve nearest sampling throughout.

Native callback/output qualification (Debug builds, disconnected scratch world):
- Run `res://src/Render/SpeedMode/Shadows/NativeLampSceneProbe.tscn` with an admitted
  Godot slot, actual engine resolution1280x720, external `GUO_LAMP_SCENE_OUT` and
  user argument `--cache-dir` pointing to its `data/fixture/cache`. Do not pass
  `--window-size`, account or login arguments. Require one ordinary native lamp
  offer, selected polar/mobile masks, stable budget0 control and positive native
  mask-overlap darkening for body/dress/mount/sitting/flip/resize. Hidden/dead
  exclusion, exact parent-off and active-resource unload must pass. Budget0vs6
  changes polar and mobile shadow work together; it does not isolate mobile-only
  attenuation. Native art/screenshots stay private.
- The corresponding WindowLight `NativeWindowSceneProbe.tscn` uses external
  `GUO_WINDOW_SCENE_OUT` and the same isolated cache convention. It compares
  native window receiver output to a Debug-only omit-draw control, retaining
  direct window light. Require actual window/ray/current-mask witnesses and
  positive HDR output; conservative zero output is an unqualified result.

Neither native fixture establishes live-shard gameplay, cinematic appearance,
production frame time or globally leak-free shutdown. Keep exit warnings with
its targeted resource assertions.
