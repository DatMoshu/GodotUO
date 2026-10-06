# Scenario Runner

Record deterministic gameplay scenarios at publishable quality with AI or human drivers.

## Quick start

```bash
# Run a scenario with the AI driver (autonomous)
python tools/scenario_run/run.py client.login.basic --driver ai

# Follow along with the human driver (overlay, manual verification)
python tools/scenario_run/run.py client.login.basic --driver human

# Capture one run as a Discord card
python tools/scenario_run/run.py post <run_id>

# List runs and their status
python tools/scenario_run/run.py list --scenario client.login

# Clean up old runs (keeps last 30 per scenario locally, last 200 shared)
python tools/scenario_run/run.py prune
```

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

| Kind | Purpose | Input | Expectation | Notes |
|---|---|---|---|---|
| `launch` | Start the client, editor, or shard | args: launch flags | process alive | AI only |
| `wait` | Pause the scenario | seconds | elapsed time | Human: Space to advance |
| `shot` | Capture a still image | none | frame saved | Records one PNG per step |
| `note` | Log a narrative comment | text | text in events.jsonl | No verification |
| `ui.click` | Click a gump control | control: path | ui.exists, ui.text, etc. | Uses guo_ui MCP |
| `ui.fill` | Type into a text field | control, text | ui.text | Uses guo_ui MCP |
| `ui.key` | Send keyboard input | key: name | ui state change | Uses guo_input MCP |
| `chat` | Send chat or console text | text | log or game response | Via guo_input text command |
| `tour_segment` | Run an EditorTour segment | id: segment name | EditorTour checks + frames | Editor only; surface override |
| `editor_invoke` | Call an editor MCP tool | tool, args | tool result | Editor only |
| `scene_set` | Set an editor scene property | property, value | scene state | Editor only |
| `renderdump` | Capture a render state dump | name: dump name | render_diff match | AI only; for parity checks |
| `render_diff` | Compare two render dumps | a, b, max_drawn_diff | diff below threshold | AI only; uses render_diff.bat |
| `lane` | Run a multi_client lane | lane_id | lane summary events | Multi-client playtest |

## Drivers

### AI Driver (`--driver ai`)

The runner executes every step autonomously:

1. Send the `do` action through the appropriate MCP (game, editor, etc.)
2. Poll the `expect` condition until true or timeout
3. Record video frames around the action
4. Log all events with frame-accurate timing
5. Continue to the next step or abort on fatal error

**Capture path:** Native Godot MovieWriter → PNG frames + WAV audio → ffmpeg H.264 transcoding  
**Quality:** 2560×1440 or 3840×2160 @ 60 fps, CRF 16  
**Determinism:** Perfect; frame-perfect replay

### Human Driver (`--driver human`)

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

### AI Runs: MovieWriter (deterministic)

Godot's built-in MovieWriter renders every frame without frame-skipping, ensuring perfect smoothness and repeatability.

```
run.py invokes:
  godot-console --offline=false --write-movie run.avi --fixed-fps 60
→ build/runs/<run_id>/raw/frames/*.png (MJPEG) + audio.wav
→ ffmpeg -i frames.avi -i audio.wav -c:v libx264 -crf 16 -preset slow run.mp4
→ Copy to H:\My Drive\Video\GUO\runs\<run_id>.mp4
```

**Pros:**
- Deterministic: same input → identical output frame-for-frame
- Perfect timing: no frame drops, no variable performance
- High quality: H.264 High Profile CRF 16

**Cons:**
- Slower than real-time (Godot runs at physics tickrate, not wall-clock)
- Requires Godot to run; cannot capture existing gameplay

### Human Runs: OBS (real-time)

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

**Master:** `H:\My Drive\Video\GUO\runs\<run_id>.mp4`

- One per run, regardless of driver (both AI and human runs land here)
- Mounted via `net use H: \\server\My\ Drive` or Google Drive desktop sync
- High bitrate H.264 (CRF 16) for archival and clip extraction
- Clips for Discord/social are cut from this master file via `run.py clip`

**If H: is not mounted:**
- Master stays in `build/runs/<run_id>/run.mp4` locally
- `runs.db` row has a NULL `video_path` (video pending)
- A `run.py sync` command moves the video to H: when Drive is next available

**Retention:**
- Keeps all master videos on H: (Google Drive, never auto-pruned)
- Local `build/runs/<run_id>/` folders pruned by `run.py prune` (keeps last 30 per scenario)
- Shared `D:\_agentShared\runs\guo\<run_id>\` pruned (keeps last 200)

## Output structure

### Per-run folder: `build/runs/<run_id>/`

```
build/runs/20261005_120000_client.login.basic_ai/
├── run.json                    # Manifest: scenario, commit, timings, pass/fail
├── events.jsonl                # Event log: frame-accurate timeline
├── summary.md                  # Human-readable recap (template: summary.md.example)
├── run.mp4                     # High-quality video (moved to H: after completion)
├── shot_*.png                  # Still images from steps marked `shot: true`
├── client.log                  # Game stdout (redacted)
├── editor.log                  # Godot stdout (redacted)
└── raw/                        # (AI runs only)
    └── frames/                 # MovieWriter PNG/MJPEG frames (pruned after transcode)
```

### Shared folder: `D:\_agentShared\runs\guo\<run_id>/`

```
D:\_agentShared\runs\guo\20261005_120000_client.login.basic_ai/
├── run.json                    # Copy of manifest
├── events.jsonl                # Copy of events
├── summary.md                  # Copy of summary
└── shot_*.png                  # Copy of still images
```

Shared folder is read-only to other seats; only the runner writes.

### Database: `D:\_agentShared\dbs\runs.db`

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
| video_path | TEXT | Path to master video (`H:\My Drive\Video\GUO\runs\<run_id>.mp4` or NULL if pending) |
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

Environment variables (or `config.bat`):

| Variable | Purpose | Example |
|---|---|---|
| `GUO_RUNS_DB` | Path to runs.db | `D:\_agentShared\dbs\runs.db` |
| `GUO_RUNS_SHARED_DIR` | Shared run folder | `D:\_agentShared\runs\guo` |
| `GUO_RUNS_VIDEO_DIR` | Video master location | `H:\My Drive\Video\GUO\runs` |
| `OBS_WS_PASSWORD` | OBS WebSocket password | (leave unset for no auth) |
| `UO_SHARD_HOST` | Dev shard hostname | `127.0.0.1` |
| `UO_SHARD_PORT` | Dev shard port | `2606` |
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

The watchdog will detect this after 20 seconds of no frame writes or 45 seconds of no MCP replies, and kill Godot.

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
- **Discord posting:** `tools/scenario_run/run.py post <run_id>`

---

**Status:** Build step 1 (docs) and 2 (runner + AI driver) in progress.  
**Last updated:** 2026-10-05
