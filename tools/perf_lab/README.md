# tools/perf_lab

The perf lab: GUO's frame rate on a player's own machine, as a table and a
report they can share. It wraps `tools/perf_probe` for someone who is not a
developer:

1. the doctor checks the .NET SDK, Godot, the UO client data and a running
   shard, and says how to fix what is missing;
2. GUO is built optimised once (`tools/guo/build.py`);
3. the probe runs at each preset with vsync, the frame cap and upstream's draw
   pacing off: `1080p` (1920x1080, zoom 1.0) and `1440p-far` (2560x1440, zoom
   2.5, the camera's widest);
4. it prints the FPS and frame-time table and writes
   `build/perf/perf_lab_<UTC time>.json` and `.md`.

The JSON (data_formats section 41) is the one to paste or attach in Discord:
GPU, driver, CPU and OS fields and the numbers per scene. Before it is written
it is checked for paths and for local names (the login and computer name, the
home folder, the shard's owner and game master accounts); a report that
carries one is not written.

It needs a shard where the probe account is a GM; the local dev shard
(`launchers\shard\run.bat`) is one. How to read the numbers: the wiki page
Performance (`docs/wiki/Performance.md`).

## Run

```
launchers\dev\perf_lab.bat                       both presets, every scene
launchers\dev\perf_lab.bat --preset 1440p-far --scene britain-bank
launchers\dev\perf_lab.bat --size 3440,1440 --zoom 2.0
launchers\dev\perf_lab_doctor.bat                check, measure nothing
python tools/perf_lab/run.py --report build/perf/perf_A.json   report earlier probe runs
```

## Tests

```
python -m pytest tools/perf_lab -q
```

They cover what the report keeps from the probe, the privacy check, the window
notes and the doctor's shard check; the run itself needs a shard and is the
scenario `tools/scenarios/client/perf_lab.scenario.json`.
