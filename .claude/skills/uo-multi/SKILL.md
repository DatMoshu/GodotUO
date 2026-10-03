---
name: uo-multi
description: "Author a new UO multi (house, keep, castle) and prove it in game: mine the client's buildings into a catalogue, write a description, build and validate it, write it into a staged data set in the multi range, place it on the private shard and walk through its door on camera. Never writes the install. Use for 'build a new house/keep/castle', for turning a plan into a multi, and before claiming an authored multi works."
argument-hint: "<description.json> [--no-play] [--visit X,Y,Z ...]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# UO Multi

Runs the `tools/multi` flow end to end and reports what actually happened.
The contract is `docs/data_formats.md` section 16. The staged-set rules are
ADR-0022 and the `/uo-data` skill.

**Never write under `UO_CLIENT_DATA`.** Never use the shared shard (2593),
and never take another agent's private port.

---

## 0. Before anything

```
python tools\multi\test_multi.py
python tools\uodata_write\test_uodata.py
```

- Both must print `OK`. A failure stops the flow: report the `FAIL` lines
  verbatim.
- The catalogue must exist: `build\multi\catalogue\families.json`. If it is
  missing or older than the install, run `python tools\multi\run.py mine`
  (about 10 s) and report its summary: multis by kind, buildings, storey step,
  stair rise, ground floor z.

## 1. The description

- Read the description and check its materials against `families.json`: a
  material must have the roles it is used for (walls at the wall height, a
  floor, `stair` pieces for steps, `roof` sides for the roof style).
- For a new style, look before you choose:
  `python tools\multi\run.py sheets` writes contact sheets per kind and per
  material under `build\multi\catalogue\sheets`.
- Show the owner the ones you picked from.

## 2. Build

```
python tools\multi\run.py build <description.json>
```

- Exit 0 means valid. A `PROBLEM` line is a stop: fix the description, not
  the validator.
- Open `preview.png`, `preview_noroof.png` and `plan_<n>.png` in
  `build\multi\built\<name>\` and look at them before going on. A floating
  roof, a missing corner or a wall across a door is found here more cheaply
  than on the shard.

## 3. Write into the stage

```
python tools\multi\run.py write <name>
python tools\uodata_write\run.py verify --stage build\uodata\multi
```

- It must print `read back equal`, `components equal` and
  `install unchanged: True`. Anything else is a failure: say so and stop.
- The writer only adds. A name already in the stage is refused; build it
  under a new name, or start a new stage.

## 4. In game (unless `--no-play`)

This needs this checkout's private shard on a port of its own. Set it up
once:

```
python tools\editor_shard\run.py setup --port <N>
python tools\editor_shard\run.py bridge --bridge-port <M>
```

Then:

```
powershell "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB"
python tools\multi\run.py prove <name> --clip build\multi_proof\<name>.mp4 [--visit X,Y,Z ...]
```

- Under 16 GB free: stop and report. `prove` refuses too.
- A timing run goes alone; other Godot runs may overlap.
- `prove` takes down what earlier proofs left standing, then finds a flat,
  empty site near the probe character (or takes `--at X Y Z`). It places the
  multi and its doors, then walks the client through the stops the build
  wrote: through a yard's gate, the step, the doorway, the middle of the
  ground floor, up each stair, into a room upstairs and out onto a balcony.
  It then walks to each `--visit` (multi-local x, y, z).
- A stop counts only at its x and y and within 4 of its z.
- Every stop is a frame `<stop>.png` and a dump.
- `report.json` says where the client stood. `PASS` means every stop was
  reached.

## 4b. A scene of several multis

A castle bigger than one multi is a scene (`docs/data_formats.md` section 16,
`tools/multi/examples/fort_demo.json`): walls, towers, platforms, causeways,
stairs and houses on one grid, cut into one multi per part.

```
python tools\multi\run.py scene-build <scene.json> [--cut Z]
python tools\multi\run.py scene-write <name> --stage build\uodata\<stage>
python tools\multi\run.py scene-prove <name> --stage build\uodata\<stage> --at X Y Z --clip build\multi_proof\<name>.mp4
```

- Look at `preview.png` (and `preview_below_Z.png` for the inside) first.
- Every part is placed at the site plus its centre, so the parts meet;
  then the tour is walked, with a stop added at each stair it climbs.
- Without `--at` it looks for flat, empty ground big enough near the probe
  character; a large scene usually needs a clear site given with `--at`.

## 5. Report

| Step | Result |
|---|---|
| tests | `test_multi` / `test_uodata` OK |
| build | components, doors, storeys, valid |
| write | multi id, read back, install unchanged |
| prove | site, place ack, each stop reached (and the z the client reports), clip |

- Show the frames. "Walked in through the door" is claimed only with the
  `doorway` and `inside` stops reached.
- "The roof hides" is claimed only with an inside frame that shows the
  interior.
- Confidential projects stay in the terminal. Post nothing unless the owner
  says so.

## Generators and styles

For a quick start or an editor panel, `run.py house|autowall|roof|stairs|rotate|mirror|import|export` (JSON in and out, deterministic by seed) build from the style catalogue (`run.py styles`; data_formats section 27). Mine the client's own styles with `run.py styles --mine` (local, never committed). Check the result with the validator problems it returns, then continue with build, write and prove.
