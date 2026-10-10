# tools/datasources

Which client data a run would use (ADR-0021): a custom data folder
(`UO_CUSTOM_DATA`), then the UO install, then the first-run wizard. It prints
the choice and every candidate passed over, and why. `check --bat` / `--sh`
write the fragment `launchers/game/play.bat` and `play.sh` call to set
`UO_CLIENT_DATA` (and `UO_FILES_OVERRIDE` for a layered custom folder).
`gen-cs` writes `godot/GUO/src/Bootstrap/DataRequirements.g.cs`, the runtime's
copy of the required set in `tools/guo/formats.py`. Exit 0 valid data, 3 run
the wizard.

## Run

```
python tools/datasources/run.py check [--bat FILE] [--sh FILE] [--quiet]
python tools/datasources/run.py gen-cs [--check]
```

## Tests

Covered by `python tools/guo/test_config.py` (`DataSourceTests`), which also runs `gen-cs --check` so a stale generated file fails CI.
