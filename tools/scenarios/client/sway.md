# UO+ vegetation sway (SWAY-01–03)

Layer L1; normal and export builds. No external dependency or doctor required.
Toggle: Video → UO+ effects → Tree and plant sway; default off. Density is amplitude.

1. Run `godot-console --headless --path godot/GUO res://src/Render/Sway/SwayProbe.tscn`.
   Expect `[sway] PASS`. The smoke launcher runs this line too.
   For synthetic pixel proof, set `GUO_SWAY_VISUAL` to an evidence folder and
   run the same scene with a headed renderer. It compares an atlas region on
   a translated world canvas: zero displacement must be identical; the crown
   moves while the base and neighboring sprite stay fixed.
2. In the running client, enter Yew forest and Britain gardens. With the same
   camera, record ten seconds off and ten seconds on at density 50. Inspect tree
   bases and nearby walls, roofs, signs and characters: only classified statics
   bend, with different phases. Turn Tree to stumps on: stumps must stay still.
3. Move density to 0, 50 and 100, Apply, reopen Options, reload the profile, then
   apply Defaults. Zero stops movement; persisted amplitude matches; Defaults
   leaves sway unchecked. Disable all also stops movement immediately.
4. With sway on, compare dry weather and upstream rain/storm Count 70. Rain has
   2.5× dry wind amplitude. Snow, missing weather and unrelated effects do not
   strengthen wind. Toggle rain rings off: rain still drives sway.
5. Optional reproducible editor capture: start the editor with
   `-- --guo-editor-smoke OUT --guo-editor-world-shot 0,750,1000,1280,720
   --guo-editor-sway-proof 300`. OUT is an evidence directory outside the repo.
   Repeat at Britain gardens (`0,1520,1460,1280,720`). The editor uses an unsaved
   scratch profile. `sway-off/` and `sway-on/` contain 300 frames at 30 fps;
   `sway-cost.json` reports warmed CPU draw submission medians, excluding image
   IO. This proves the game renderer against native map data, without a shard;
   it does not replace live client/persistence acceptance or isolate GPU cost.
6. Measure on/off at the editor view with the same renderer, dimensions and
   scene. Report CPU/GPU scope, sample count and cache warm-up. Target added
   cost <0.5 ms. Never infer a performance pass from the contract probe.

Off-state parity: compare a fixed-camera frame against the base branch, and
against density zero/Disable all. Document pixel counts and renderer. All
captures remain private evidence; no proprietary UO art is committed.
