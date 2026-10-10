# tools/uodata_write

Authors UO data files into a staged set, never the install (ADR-0022): free
slot scans, a pack's reserved ranges (item ids, animation bodies with their
paperdoll gumps, multi ids) recorded in `<stage>/slots.json`, writing packs'
records, and a hash check that every copied install file is unchanged. Range
policy comes from `ranges.json`, with a shard maintainer's `--ranges FILE` (or
`UO_DATA_RANGES`) merged over it. `uodata.py` holds the readers and writers;
`play.py` and `outfit_video.py` show staged content in the client.

## Run

```
python tools/uodata_write/run.py scan    --stage DIR
python tools/uodata_write/run.py reserve --stage DIR --pack NAME [--statics N] [--bodies N] [--multis N]
python tools/uodata_write/run.py verify  --stage DIR
python tools/uodata_write/run.py outfit  --stage DIR --source LAB [--pack NAME]
```

## Tests

`python tools/uodata_write/test_uodata.py` builds a tiny fake install in a temporary folder and checks the stage, the scanners, the range registry, the writers and that the install is byte for byte unchanged. No client data needed; CI runs it.
