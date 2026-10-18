# tools/perf_probe

Frame time in fixed scenes (Epic B, B1): runs the desktop client with
`--perf-probe`, windowed and never taking the focus, against a shard where the
probe account is a GM. It measures the login screen, an open field, the
Britain bank, a dense forest and a dungeon, then a run through ground loading
into view, and writes `build/perf/perf_<label>.md` and `.json`. Frame times
compare only on one machine; draw calls and batcher counts compare anywhere.
On Android, the Deck or the web run the client with the same flags.

## Run

```
python tools/perf_probe/run.py [--label NAME] [--port PORT] [--args "--batched-world"] [--size 1280,720]
python tools/perf_probe/run.py --compare A B
```

R3's opt-in `--perf-probe --perf-hd-acceptance --perf-scene britain-bank`
keeps the client open after reports for a settled-state scenario still. It
requires explicit fixed scenes, refuses parity/walk, warms for at least ten
monotonic seconds and samples 600 rendered frames per scene. Without it the
existing 180-frame settle/360-frame sample and quit behavior is unchanged.
Do not use MovieWriter for timing: run the dense lighting scenario with
`--no-record`; record a separate showcase session afterward.

Acceptance JSON includes actual warmup/sample elapsed milliseconds, sample
count, GPU/vendor/driver/API/backend identity and root/light/PostFx viewport
RIDs with CPU/GPU means and positive observation counts. Absent viewports and
unavailable/nonpositive/nonfinite readings are `null`, with an explicit status;
headless output is never GPU proof. GPU times may be delayed by the backend.
The scenario's labeled still step follows completed report writing, outside
the measured window. Compare identical frozen private-world/profile states.

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed.
