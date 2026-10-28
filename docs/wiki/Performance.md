# Performance

How fast GUO runs on your machine, measured the same way on every machine,
and how to share the numbers so they can be compared with ours.

This page describes the existing measurement tooling. Historical results
apply only to their recorded builds and conditions; they do not establish the
current build's speed relative to ClassicUO. Start with
[Renderer and Performance](Renderer-and-Performance.md) for current selection,
canvas recovery and interpretation limits. Read the
[performance gate](../perf/performance_gate.md) before comparing results.

## Run the perf lab

You need GUO set up as in [Getting Started](Getting-Started.md) and the local
dev shard running (the lab plays on a shard where its account is a game
master; [Dev Shard](Dev-Shard.md) sets it up):

```bat
launchers\shard\run.bat          REM in one terminal; leave it running
launchers\dev\perf_lab.bat       REM in another
```

The lab first checks what it needs. When something is missing it says what
and how to fix it; `launchers\dev\perf_lab_doctor.bat` runs only that check.
Then it builds GUO optimised (as a release is) and runs the client twice,
each time in a window that stays in the background:

| Preset | Window | Zoom |
|---|---|---|
| `1080p` | 1920x1080 | 1.0, the default view |
| `1440p-far` | 2560x1440 | 2.5, the camera's widest, where GUO costs most |

Each run visits the same places with a game master's `[go`: the login
screen, open grassland south of Britain, the Britain bank, the forest east of
Yew, the mouth of Despise, and twelve seconds of running through ground that
loads as it comes into view. Vsync and the frame cap are off for the
measurement, and the post-processing look is off, so the numbers are the
renderer's own. A run takes a minute or two. Leave the machine alone while it
runs: anything else busy on it shows up in the frame times.

Some options:

```bat
launchers\dev\perf_lab.bat --preset 1440p-far                  REM one preset
launchers\dev\perf_lab.bat --scene britain-bank                REM one scene (repeatable)
launchers\dev\perf_lab.bat --size 3440,1440 --zoom 2.0          REM your own size
```

A window larger than your screen is shrunk to fit; the report says so.

## Share it

The lab prints a table and ends with the line `share this file`. That file,
`build\perf\perf_lab_<time>.json`, is short enough to paste in a Discord
message, or attach it. It holds:

- the GPU, its driver version and the graphics API, the CPU and its thread
  count, the memory, the OS version, the screen size and refresh rate;
- the GUO version (a commit) and the engine version;
- per preset and scene, the numbers in the table below.

It holds no paths and no names: before it is written it is checked for your
login and computer name, your home folder and the shard's accounts, and a
report that carries one is not written. A table of the same numbers is beside
it as `.md`.

## Read it

| Column | Means |
|---|---|
| FPS | Frames per second: 1000 / mean ms. |
| mean ms | The average frame time. Lower is better. |
| p95 ms, p99 ms | The slow frames: 95% and 99% of frames were at least this fast. A high p99 with a good mean is stutter. |
| draw calls | Draws the engine sent to the graphics card per frame. |
| batcher items | Batches GUO's renderer opened per frame; each one breaks a batch. |
| texture switches | Times per frame the renderer changed texture. |
| world prepare ms | GUO's CPU time sorting and culling the world each frame. |
| world draw ms | GUO's CPU time handing the sorted world to the engine. |
| GPU ms | The graphics card's time per frame. |

What to look for:

- **Frame times compare only on one machine at one window size.** Draw calls,
  batcher items and texture switches depend on the scene, not the machine:
  they compare anywhere, and should match ours to within a few percent.
- When world prepare and world draw add up to most of the mean, the CPU is the
  limit, which is what we see on every machine so far. When GPU ms is close to
  the mean, the graphics card is.
- The login scene is a sanity check: hundreds of FPS means the run was
  uncapped as it should be.

On an RTX 4090 with a Ryzen 7 7800X3D (2026-10-18), the Britain bank ran at
136 FPS at 1920x1080 zoom 1.0 and 13.6 FPS at 2560x1440 zoom 2.5, with
under 1 ms a frame on the GPU.

## For developers

The lab wraps the probe (`tools\perf_probe`, `launchers\dev\perf_probe.bat`),
which writes every column it measures to `build\perf\perf_<label>.json`; the
lab's report keeps the ones above. The report's format is
`docs/data_formats.md` section 41. `python tools\perf_lab\run.py --report
FILE...` makes a shareable report from probe runs already measured.
