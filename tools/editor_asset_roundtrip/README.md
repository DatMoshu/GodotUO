# tools/editor_asset_roundtrip

Proves a world project's asset edits reach the client (ADR-0020, editor phase
5): the replaced art, gumps and hues are exported as a patch set by
`tools/world`, verified, then the real client starts headless with
`--asset-probe` on that export and decodes the replaced ids, which are compared
with the project's files pixel for pixel. A control run without the override
must decode the install's art instead. No shard and no window. Output goes to
`build/editor_asset_roundtrip/`.

## Run

```
python tools/editor_asset_roundtrip/run.py [--project DIR] [--out DIR] [--no-build]
```

## Tests

No unit tests: this is the proof. Exit 0 every asset round-tripped, 1 a mismatch or a failed step, 2 bad input.
