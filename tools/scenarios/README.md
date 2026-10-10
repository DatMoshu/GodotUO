# tools/scenarios

The scenario files `tools/scenario_run` plays: one JSON file per scenario,
grouped by area (`client/`, `editor/`, `shard/`), validated against
`schema/scenario.schema.json`. `schema/event.schema.json` and
`schema/run.schema.json` describe the event log and run record each run
writes. The scenario format and the list of scenarios are in
`tools/scenario_run/README.md` and `docs/data_formats.md`.

## Run

```
python tools/scenario_run/run.py validate editor.tabs.sweep
python tools/scenario_run/run.py list
```

## Tests

`python -m pytest tools/scenario_run` checks the runner against these schemas; `validate` checks one scenario file.
