# Scenario Runner

Record deterministic gameplay scenarios at publishable quality with AI or human drivers.

## Quick start

What the runner on main does today (build steps 2 and 3):

```bash
# Run a scenario with the AI driver (autonomous; the default driver)
python tools/scenario_run/run.py editor.tabs.sweep

# Check scenario files without running anything
python tools/scenario_run/run.py validate editor.tabs.sweep

# List scenarios, then the registry's recent runs; optionally only scenarios whose id starts with a prefix
python tools/scenario_run/run.py list
python tools/scenario_run/run.py list --scenario editor.

# Clean up old run folders: newest 30 per scenario under build/runs, newest 200 per project in the shared folder
python tools/scenario_run/run.py prune [--keep-local N] [--keep-shared M] [--dry-run]

# Build the Discord card for a finished run: writes card.json beside its run.json and prints it
python tools/scenario_run/run.py post <run_id>
```

`post` reads the run's `run.json` (the repo-local `build/runs/<run_id>/`, else the shared copy) and writes `card.json`
next to it: title, scenario, driver, PASS/FAIL, failed step ids, duration, commit and the stills by name, plus a text
body under 900 characters (a long failed-step list ends in "+N more"). Every text field is redacted first. An unknown
run id exits 2. **It builds the card and nothing else:** no network, no Discord call, no token. Sending is a separate,
explicit step owned by the integrator after Moshu has watched the run (standing rule).

`prune` deletes only folders whose `run.json` names the folder's own run id, keeps any folder that holds a video
file, never reads the video folder, and leaves every registry row in place (they are history).

`--shard TARGET` runs a **client** scenario against a named shard instead of the one in `UO_SHARD_HOST` / `UO_SHARD_PORT`.
It reads `GUO_SHARD_<TARGET>_HOST` and `GUO_SHARD_<TARGET>_PORT` (target upper-cased, `-` and `.` as `_`; environment, then
`config.local.bat`, then `config.bat`) and the login from `GUO_SCENARIO_ACCOUNT` (or `--var account=`) and
`GUO_SCENARIO_PASSWORD`. The password comes from the environment only: `--var password=` still works but warns and
names the variable, since a command line shows in the process list and the shell history. A missing one stops the run before launch (exit 2) with the setting's name; the
values are never printed or logged. The run's manifest records the target as `shard`.

Not yet (the commands exist in the plan, not in the runner):

| Command | Arrives with |
|---|---|
| `--driver human` (exits 2 with a message) | step 5 |
| `clip` (cut a clip from the master) | not scheduled |

## Scenario files

Scenarios live in `tools/scenarios/<area>/` as JSON files validated against the schema in the same folder.

### Example

```json
{
  "id": "client.login.basic",
  "title": "Log in, reach Britain bank",
  "surface": "client",
  "requires": {"shard": "editor_shard", "account": "UO_SHARD_GM_ACCOUNTS[0]", "build": "debug"},
  "timeouts": {"step_s": 30, "run_s": 600},
  "steps": [
    {"id": "launch", "do": {"kind": "launch", "args": ["--offline=false"]}, "say": "Start GUO."},
    {"id": "login", "do": {"kind": "ui.click", "control": "LoginGump/Connect"}, "say": "Press Connect.", "expect": {"ui.exists": "CharacterListGump"}},
    {"id": "walk", "do": {"kind": "chat", "text": "[go 1434 1697"}, "expect": {"world.position": {"x": 1434, "y": 1697, "tolerance": 2}}}
  ]
}
```

See `docs/data_formats.md` for the full schema and step kind reference.

## Step kinds

On main today (step 2 and step 3 kinds):

| Kind | Purpose | Input | Expectation | Notes |
|---|---|---|---|---|
| `launch` | Start the editor | args: launch flags | process alive | AI only |
| `wait` | Pause the scenario | seconds | elapsed time | |
| `shot` | Capture a still image | none | frame saved | One PNG per step |
| `note` | Log a narrative comment | text | text in events.jsonl | No verification |
| `ui.click` | Click a control (client) | `control` (selector), `button`, `clicks`, `within_s` (how long to wait for the control, default 5) | ui.exists, ui.text, etc. | `guo_ui` to find it, `guo_input` to click its centre |
| `ui.fill` | Click a field, then type into it (client) | `control`, `text`, `clear` (BackSpace presses first) | ui.* on something the typing changes | The typed text is never logged or read back (the game omits editable values) |
| `ui.key` | Press and release a key (client) | `key` (Godot name: Enter, Escape, F1), `shift`, `ctrl`, `alt` | ui state change | `guo_input` |
| `chat` | Say a line in game (client) | `text` (a `[command` works) | log or world state | Enter, text, Enter |
| `tour_segment` | Run an EditorTour segment | id: segment name | EditorTour checks + frames | Editor only; surface override |
| `editor_invoke` | Run an F3 action by key | key, query | tool result | Editor only; not pre-approved, so the step **fails at once** with a message instead of waiting on the approval dialog (nobody is at the PC in a scripted run). Use a `tour_segment`, or run it by hand |

Not yet (a scenario that uses one fails at the step today):

| Kind | Purpose | Arrives with |
|---|---|---|
| `scene_set`, `renderdump`, `render_diff` | Editor scene and render-parity steps | not scheduled |
| `lane` | Run a multi_client lane | step 7 |

### Controls

A `control` is a string (matches a control whose type, name or text equals it: `"Connect"`) or an object with any of
`type`, `name`, `text`, `contains` (substring of the text), `system` (`classic` or `godot`) and `index` (which match,
0 based). It is matched against the `guo_ui` snapshot, so `ui.click` finds the control by what it is, not by pixels.

### Expectations

An `expect` block is polled until every condition holds or `within_s` runs out (the step fails then, and the event
log keeps what was observed). Conditions: `ui.exists` (a control), `ui.absent`, `ui.text` (`{"control", "equals" | "contains"}`,
labels and buttons only), `ui.count` (`{"control", "equals" | "at_least"}`), `world.position` (`{"x","y","z"?,"map"?,"tolerance"}`,
from `guo_state`), `scene` (the client's scene class, e.g. `LoginScene`), `log.contains` (the program's log), plus the
editor's `result`, `editor.state` and `file.exists`.

## Editor scenarios

| Scenario id | File | What it covers |
|---|---|---|
| `editor.smoke.layout` | `tools/scenarios/editor/smoke_layout.scenario.json` | launch, layout, Art tab |
| `editor.tabs.sweep` | `tools/scenarios/editor/tabs_sweep.scenario.json` | tour segments `layout`, `art`, `gumps`, `anims`, `store`, `maps`, `pick`, `shard`; stills on art, store, pick |
| `editor.anim.roundtrip` | `tools/scenarios/editor/anim_roundtrip.scenario.json` | `anims`, then `anim_roundtrip` (Pixelorama and back), still at the end |

**Gap:** the sweep does not visit the MapGen tab or the Multis authoring tab. EditorTour has no segment for either
(its `multis` segment only browses Multis under Assets), and adding one is editor code, outside the scenario files.

## Shard scenarios

| Scenario id | File | What it covers |
|---|---|---|
| `shard.console` | `tools/scenarios/shard/console.scenario.json` | log in as the shard's owner account, type an owner command (`[go`), check the position; stills at login and in the world |

Run it with `--shard <target>`; the account must already have a character (a fresh owner account has none: make one
once with `launchers\game\play.bat --play --account A --password P --shard-command "[Where"` against that shard). It is
for a throwaway shard such as the muo_shard container (`tools/muo_shard/README.md`, Container run with the client).

## Drivers

### AI Driver (`--driver ai`)

The runner executes every step autonomously. A client run records video (see Recording); an editor run records stills and
events only.

1. Send the `do` action through the appropriate MCP (game, editor, etc.)
2. Poll the `expect` condition until true or timeout
3. Record video frames around the action
4. Log all events with frame-accurate timing
5. Continue to the next step or abort on fatal error

**Capture path:** the engine's MovieWriter (`--write-movie run.avi --fixed-fps 60`, MJPEG + PCM audio) → ffmpeg H.264 High, CRF 16, AAC  
**Quality:** 60 fps, every frame rendered whatever the machine can do. The size is the engine window's at start, 1280×720 from `project.godot` (Godot's `--resolution` does not move it); a 1440p or 4K master needs a project-level size, which is not done yet.  
**Scope tonight:** the client surface. An editor run records stills and events only (the editor is not a MovieWriter target); OBS and desktop capture are not implemented.

### Human Driver (`--driver human`) (not yet: step 5)

Planned design; the runner refuses `--driver human` today.

The runner shows each step on an in-engine overlay and waits for human verification:

1. Skip the `do` action (human takes control)
2. Display the step caption and highlights on TourOverlay
3. Poll the same `expect` condition
4. When true, auto-advance (or Space to skip, Esc to abort)
5. Record overlay, audio and keystrokes

**Capture path:** OBS (WebSocket) or ffmpeg desktop capture  
**Quality:** Same as AI (2560×1440 @ 60 fps, CRF 16)  
**Determinism:** Real-time; events include observer timestamps for syncing

Steps marked `ai_only: true` are skipped in human mode and logged as such.

## Recording: capture paths and conventions

On main today: the AI driver's MovieWriter path (a client run records by default; the master is `run.mp4`, copied to the
video folder when one is mounted). Not yet: the human driver's OBS and ddagrab paths and `run.py clip`.

### AI Runs: MovieWriter (deterministic)

`run.py <client scenario>` records by default (`--no-record` for stills and events only). Before it launches anything the runner
checks what the run needs (10 GB free for a recording, ffmpeg on PATH, the video folder mounted if one is configured, the shard
answering if the scenario `requires.shard`) and stops with the reason, so a run never starts and then dies for a known cause.
The client is started through `launchers/game/play.bat` with `GUO_ENGINE_ARGS` carrying the engine flags, audio on. At the end the
runner asks the client to quit (`guo_quit`) so the movie is finalised, transcodes, copies the master to `GUO_RUNS_VIDEO_DIR` when
that folder is mounted, and deletes the raw AVI. Event `frame` on the client is the engine frame, which in a recording is the movie
frame, so an event's video time is `frame / 60`.

Godot's built-in MovieWriter renders every frame without frame-skipping, ensuring perfect smoothness and repeatability.

```
run.py invokes:
  godot-console --offline=false --write-movie run.avi --fixed-fps 60
→ build/runs/<run_id>/raw/frames/*.png (MJPEG) + audio.wav
→ ffmpeg -i frames.avi -i audio.wav -c:v libx264 -crf 16 -preset slow run.mp4
→ Copy to <GUO_RUNS_VIDEO_DIR>/<run_id>.mp4
```

**Pros:**
- Deterministic: same input → identical output frame-for-frame
- Perfect timing: no frame drops, no variable performance
- High quality: H.264 High Profile CRF 16

**Cons:**
- Slower than real-time (Godot runs at physics tickrate, not wall-clock)
- Requires Godot to run; cannot capture existing gameplay

### Human Runs: OBS (real-time) (not yet: step 5)

OBS records the live Godot window via game capture, preserving latency and operator input.

#### OBS Setup

**Scene preset name:** `GUO run`

**Configuration:**
1. Game capture source targeting the Godot editor or client window
2. Resolution: 2560×1440 (client) or 3840×1500 (editor)
3. Frame rate: 60 fps (match game's target)
4. Encoder: NVIDIA NVENC (H.264) or Intel QSV if available, fallback x264
5. Bitrate: 20–30 Mbps (CRF 16 quality equivalent)
6. Audio sources: game audio + microphone (if narrating)
7. Output file: `build/runs/<run_id>/run.webm` (or `.mkv`)

**WebSocket server:** Enable in OBS → Tools → WebSocket Server (default port 4455)

**Runner integration:**
```python
# Connect to OBS WebSocket
client = obsws_python.ReqClient(host="127.0.0.1", port=4455, password=os.environ.get("OBS_WS_PASSWORD"))

# Start recording the GUO run scene
client.recording.start_recording()

# Log event timestamps for sync
event = {"kind": "shot", "recorder_ts_ms": client.utils.get_current_time_in_milliseconds()}

# Stop after run completes
client.recording.stop_recording()
```

**Fallback:** If OBS is unavailable, use ffmpeg `ddagrab` (Desktop Duplication API):
```bash
ffmpeg -f ddagrab -drawbox 0 -offset_x 0 -offset_y 0 -video_size 2560x1440 -framerate 60 \
  -i desktop -c:v hevc_nvenc -c:a aac -b:a 192k output.mp4
```

#### Video location

**Master:** `<GUO_RUNS_VIDEO_DIR>/<run_id>.mp4`

- One per run, regardless of driver (both AI and human runs land here)
- A synced or mounted folder, set in `GUO_RUNS_VIDEO_DIR`
- High bitrate H.264 (CRF 16) for archival and clip extraction
- Clips for Discord/social are cut from this master file via `run.py clip`

**If `GUO_RUNS_VIDEO_DIR` is not set or not reachable:**
- Master stays in `build/runs/<run_id>/run.mp4` locally
- `runs.db` row has a NULL `video_path` (video pending)
- Moving it to the video folder later is manual (no `sync` command exists yet)

**Retention:**
- Keeps all master videos in `GUO_RUNS_VIDEO_DIR` (never auto-pruned)
- Local `build/runs/<run_id>/` folders pruned by `run.py prune` (keeps last 30 per scenario; a folder holding a video file is kept)
- Shared `<GUO_RUNS_SHARED_DIR>/<run_id>/` pruned (keeps last 200 per project)

## Output structure

### Per-run folder: `build/runs/<run_id>/`

```
build/runs/20261005_120000_client.login.basic_ai/
├── run.json                    # Manifest: scenario, commit, timings, pass/fail
├── events.jsonl                # Event log: frame-accurate timeline
├── summary.md                  # Human-readable recap (template: summary.md.example)
├── run.mp4                     # High-quality video (copied to the video folder after completion)
├── shot_*.png                  # Still images from steps marked `shot: true`
├── client.log                  # Game stdout (redacted)
├── editor.log                  # Godot stdout (redacted)
└── raw/                        # (AI runs only)
    └── frames/                 # MovieWriter PNG/MJPEG frames (pruned after transcode)
```

### Shared folder: `<GUO_RUNS_SHARED_DIR>/<run_id>/`

```
<GUO_RUNS_SHARED_DIR>/20261005_120000_client.login.basic_ai/
├── run.json                    # Copy of manifest
├── events.jsonl                # Copy of events
├── summary.md                  # Copy of summary
└── shot_*.png                  # Copy of still images
```

Shared folder is read-only to other seats; only the runner writes.

### Database: `<GUO_RUNS_DB>`

SQLite database tracking all runs across all projects.

**Table: `runs`**
| Column | Type | Purpose |
|---|---|---|
| run_id | TEXT PRIMARY KEY | Run identifier (timestamp + scenario + driver) |
| project | TEXT | "guo" (for GUO runs) |
| scenario | TEXT | Scenario ID (e.g., "client.login.basic") |
| driver | TEXT | "ai" or "human" |
| commit_hash | TEXT | Git short SHA of the build |
| started | TEXT | ISO-8601 timestamp |
| ended | TEXT | ISO-8601 timestamp |
| ok | INTEGER | 1 if all steps passed, 0 if any failed, NULL if incomplete |
| steps_total | INTEGER | Total steps in scenario |
| steps_failed | INTEGER | Number of failed steps |
| video_path | TEXT | Path to master video (`<GUO_RUNS_VIDEO_DIR>/<run_id>.mp4` or NULL if pending) |
| summary | TEXT | First 256 chars of summary markdown |

**Table: `run_steps`**
| Column | Type | Purpose |
|---|---|---|
| run_id | TEXT | Foreign key to `runs` |
| step | TEXT | Step identifier from scenario |
| seq | INTEGER | Step sequence number |
| ok | INTEGER | 1 if passed, 0 if failed |
| dur_ms | INTEGER | Duration in milliseconds |
| kind | TEXT | Step kind |
| skipped | INTEGER | 1 when the step was marked for the other driver |

## Configuration

Each setting resolves in one order: environment variable, then `launchers/_shared/config.local.bat` (yours,
gitignored; start from `config.local.bat.example`), then `launchers/_shared/config.bat`. None has a default path.
A run with `GUO_RUNS_DB` or `GUO_RUNS_SHARED_DIR` unset prints one line naming the unset setting(s) and writes a
`warn` event saying registration and the shared copy were skipped; the run folder under `build/runs` is kept.

| Variable | Purpose | Example |
|---|---|---|
| `GUO_RUNS_DB` | Path to runs.db | `<set in config.local.bat>` |
| `GUO_RUNS_SHARED_DIR` | Shared run folder | `<set in config.local.bat>` |
| `GUO_RUNS_VIDEO_DIR` | Video master location | `<set in config.local.bat>` |
| `OBS_WS_PASSWORD` | OBS WebSocket password | (leave unset for no auth) |
| `UO_SHARD_HOST` | Dev shard hostname | `127.0.0.1` |
| `UO_SHARD_PORT` | Dev shard port | `2606` |
| `GUO_SHARD_<TARGET>_HOST` / `_PORT` | A named shard for `--shard TARGET` | `127.0.0.1` / `2593` |
| `GUO_SCENARIO_ACCOUNT` / `_PASSWORD` | The login `$account` / `$password` stand for | (environment only) |
| `UO_SHARD_GM_ACCOUNTS` | CSV of GM account names | `gm1,gm2,gm3` (in env as JSON array) |

## Testing

Run the test suite before committing:

```bash
pytest tools/scenario_run/test_run.py -v

# Specific test
pytest tools/scenario_run/test_run.py::test_scenario_validation -v
```

Tests cover:
- Scenario JSON schema validation
- Event log format
- Watchdog timeouts and hang detection
- Manifest generation
- Database registration

## Debugging

### A run hangs

The watchdogs: a recording whose movie file does not grow for 20 s, an MCP call with no reply for 45 s, or a program that exits
is a hang; the runner kills the program it started (only that), writes a `hang` event and exits 3. If the runner itself wedges,
a timer armed at `run_s` plus 60 s kills the program and exits 4.

- Check `events.jsonl` for the `kind: hang` event and which step was running
- Confirm the shard is reachable: `ping <UO_SHARD_HOST>`
- Check Godot logs for errors or deadlocks
- If reproducible, save the `raw/frames/` folder for frame inspection

### A step expectation fails

1. Open `run.json` and find the failing step
2. Check `events.jsonl` for the step's `expect` event and the observed vs. expected value
3. Jump to that frame number in `run.mp4`
4. Inspect the game state at that moment (gump text, player position, etc.)
5. Verify the expectation condition in the scenario file matches the intent

### OBS not recording

1. Confirm OBS is running and the WebSocket server is enabled (Tools → WebSocket Server)
2. Check firewall: `netstat -an | findstr 4455`
3. Verify the scene "GUO run" exists and has the right capture source
4. Fallback to ffmpeg `ddagrab` (set `GPORTAL_USE_FFMPEG_CAPTURE=1`)

## Related

- **Scenario format:** `docs/data_formats.md` (section "Scenario runs")
- **JSON schema:** `tools/scenarios/schema/scenario.schema.json`
- **EditorTour segments:** `godot/GUO/addons/guo_editor/EditorTour.cs`
- **DirectorDeck Runs view:** (fablehelper project)
- **Discord card:** `run.py post <run_id>` builds `card.json`; sending it is not part of the runner

---

**Status:** Build steps 1 (docs) and 2 (runner + AI driver, prune, list filter) are on main.  
**Last updated:** 2026-10-06
