# editor.staticstudio.repaint (R4b)

Layer L3, `#if TOOLS`; uses the editor's `guo/ai/enabled` preference. This is
a headless smoke scenario, run by `tools/editor_smoke`, rather than a
scenario_run JSON script. It needs the configured UO install and a Godot
runtime lease; all generation uses local deterministic stubs, with no GPU,
paid service, real ComfyUI server or shard required.

Run `python tools/editor_smoke/run.py --reload --out <new-evidence-folder>`.
Require exit 0 and `report.json` `studio.ok` plus `reloaded: true`.

The StaticStudio stage binds a static, sends its PNG and prompt through the
ComfyUI stub, fills the gallery, saves an atlas variant/theme/provenance,
applies a theme zone and clears it. It then starts an in-flight fake provider
and disables AI. Check hidden generation controls, zero workflow discovery,
no request pixels/provider invocation, cleared gallery and restored preview,
cancelled token, and working manual staged placement/theme restoration.
Re-enable AI and finish the old provider successfully: its result must be
discarded. Rebinding and closing the dock must also cancel and refuse late
gallery updates. Provider errors must name the ComfyUI doctor without exposing
raw endpoint or file details. Both passes must survive the assembly reload.

Inspect the `guo_R4b_staticstudio-variant_still_*.png` written by this stage;
it is the stub image, fitted with nearest sampling to the bound static. All
outputs derive from local client data and stay in the evidence folder.

Smoke line: `python tools/editor_smoke/run.py --reload`.
Doctor: `launchers/comfy/doctor.bat` / `doctor.sh`; add `--validate` for
explicit server node-type checks. Installation stays in `setup_studio`.
Real inference remains a separate integration check. R4c client removals are
outside this scenario.
