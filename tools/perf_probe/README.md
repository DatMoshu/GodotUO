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

## Compare counts against a baseline

```
python tools/perf_probe/gate.py --baseline docs/perf/baseline_ci.json --report <measured-report.json> --summary <summary.md>
```

The comparator accepts the client's existing PerfProbe JSON format. Draw calls
may rise by **at most 5%** in each scene; batcher items may **not rise at all**.
Mean, p95 and p99 frame times appear in a separate advisory table and cannot
fail the count comparison. `--summary` appends Markdown, suitable for a CI
step summary. Exit 0 means the count comparison passed, 1 means a count
regressed, and 2 means an input was invalid or incomparable.

Both documents must contain `optimized: true`, matching nonempty `window`
and `postfx` strings, and a `scenes` array containing exactly one each of
`login`, `open-field`, `britain-bank`, `dense-forest`, `dungeon`, and `walk`.
Each scene needs positive finite numbers for `draw_calls`, `batcher_items`,
`mean_ms`, `p95_ms`, `p99_ms` and `draws_per_frame`; draws per frame must be
at least 0.95, and p95 must not exceed p99. `frames` must be an integer of
at least 360 for stationary scenes. The clock-timed `walk` needs at least
one measured frame and a positive integer `tiles_moved`. Missing, duplicate,
extra, idle or unoptimized scenes fail validation. Additional probe fields
are allowed and ignored by the comparator.

A real CI baseline and probe job are **not yet provisioned**. The example
baseline path above is the reserved PERF-02 location, not a checked-in sample
measurement. Do not replace a real measurement with synthetic values or use
desktop counts as a CI baseline. A deliberate baseline commit must identify
the runner, source revision, client data version, shard fixture, renderer,
window size, zoom and flags. Keep proprietary files and account credentials
on the approved runner, outside repository artifacts. The current probe JSON
does not record zoom, renderer or world-fixture identity, so the runner must
pin these separately before a live gate can be trusted. A login-only run
cannot pass this comparator.

## Tests

```
python -m pytest -q tools/perf_probe/test_gate.py
```

The comparator tests use synthetic numbers to check threshold boundaries,
invalid inputs, advisory timings, exit codes and summary output. They make
no claim about renderer speed. The actual probe still needs a windowed
Godot client, a client data install and a GM account on a shard.
