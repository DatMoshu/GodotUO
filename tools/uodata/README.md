# tools/uodata

Inspects and verifies the UO client data the port reads in place: proves the
install is complete and readable before the runtime uses it, and writes the
manifest the runtime and the port audit read. Never copies or converts the
install.

## Run

```
python tools/uodata/run.py verify [--data-dir DIR] [--out manifest.json]
python tools/uodata/run.py list [--subsystem render/art]
python tools/uodata/run.py where
launchers\pipeline\01_verify_client_data.bat
```

## Tests

No unit tests (it needs a real install). The file registry it checks against is covered by `python tools/guo/test_config.py`. Exit 0 every required file present, 1 one missing or unreadable, 2 no data folder.
