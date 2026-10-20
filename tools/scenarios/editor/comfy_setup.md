# ComfyUI studio setup acceptance (R4a)

Layer: Tools supporting L3. No client changes or generation. This manual tool
scenario does not launch the editor and is not a scenario_run JSON scenario.

1. Run `python -m pytest tools/comfy/test_setup_studio.py -q`. It constructs
   synthetic models and a disposable local git pack: verify alias hashing,
   corrupt files, node revision drift, dirty trees, interrupted download
   resume, ignored Range responses, incorrect offsets and traversal rejection.
2. Point `UO_COMFY_DIR` at an existing user-owned installation and run
   `launchers/comfy/doctor.bat` (or `.sh`). It must only read files, report
   missing/drifted dependencies and return 1 when required entries fail.
   Managed voice and external MoGe weights must say UNVERIFIED.
3. In a disposable installation, run `setup_studio` only when downloads are
   desired. Confirm exact node HEADs, install requirements with that install's
   Python, restart ComfyUI and run `doctor --validate`. No prompts are queued.
4. Change one model byte or node source file. Doctor must fail. Setup must
   leave the existing file or pack untouched and explain manual repair.

The automated smoke line is step 1. Steps 2-4 against real installations are
optional integration acceptance and must not be represented as an inference
proof. Missing external dependencies name the ComfyUI doctor/setup launchers.
