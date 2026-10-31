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

Windows launcher regression: `python -m pytest tools/guo/test_verifier_launcher.py`. It runs real `cmd.exe` with synthetic filenames (no game assets), covering spaces, trailing separators, local config, missing folders and explicit overrides. Configuration tests: `python tools/guo/test_config.py`. Exit 0 means every required file is present, 1 means a required file is missing or unreadable, and 3 requests the first-run wizard when no data folder exists.

`UO_CLIENT_DATA` may end with a backslash or forward slash. The Windows verifier reads it directly from the configured environment rather than quoting it into Python arguments, so a final backslash cannot swallow the following options. Explicit `--data-dir` remains available; when typing a quoted Windows path at the command line, omit its final backslash or use forward slashes.
