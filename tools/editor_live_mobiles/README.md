# tools/editor_live_mobiles

Proves the editor's Live map layer feed on the private shard (ADR-0027). It
starts the private shard with the bridge (`tools/editor_shard`, never the dev
shard), logs a probe player in, speaks the bridge's `mobiles` op over TCP
(fields, rate limit, a non-staff `as`, an oversized rectangle), then opens a
headless editor with `--guo-editor-live mobiles` and checks the Live layer
holds the player. Writes `build/editor_live_mobiles/report.json`.

## Run

```
python tools/editor_live_mobiles/run.py [--shard-port 2615] [--bridge-port 2616]
```

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed. Exit 0 every check passed, 1 a check failed, 2 a step could not run.
