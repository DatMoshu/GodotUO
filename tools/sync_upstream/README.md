# tools/sync_upstream

Keeps the read-only ClassicUO reference current and reports which upstream
commits touched files the port has already ported, the changes that silently
rot. The reviewed pin lives in `docs/upstream/UPSTREAM_PIN.json`. Nothing
under `sources/` is ever edited.

## Run

```
python tools/sync_upstream/run.py            update and report drift
python tools/sync_upstream/run.py --pin      mark current upstream as reviewed
python tools/sync_upstream/run.py --no-fetch report without touching the network
python tools/sync_upstream/run.py --at-pin   check out the reviewed pin (bootstrap)
launchers\dev\sync_upstream.bat
```

## Tests

No unit tests. `tools/port_drift`, which CI runs, measures how far each ported file has drifted from its upstream original.
