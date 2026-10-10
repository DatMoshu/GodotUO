# tools/port_errors

Measures what is left by building every area at once: it builds
`GUO.csproj` with `-p:GuoAllAreas=true`, which turns its staged exclusions
off, forces a full rebuild (an incremental build
under-reports badly), de-duplicates MSBuild's repeated lines and groups the
errors by the missing symbol, so the output is a list of things left to port.

## Run

```
python tools\port_errors\run.py
launchers\dev\port_errors.bat
```

## Tests

No unit tests; the compiler is the check. The project file is never edited.
