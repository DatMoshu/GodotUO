# tools/port_bulk

Ports `verbatim`- and `shim`-tier files from ClassicUO into the Godot project,
identically every time: the namespace, the `using` lines, the move. It never
touches rewrite-tier files and never reformats, renames, modernises or fixes
bugs, so the port stays mergeable with upstream.

## Run

```
python tools/port_bulk/run.py --area Utility [--area IO] [--dry-run]
python tools/port_bulk/run.py --all-mechanical
python tools/port_bulk/run.py --area Game --tier verbatim
launchers\pipeline\04_port_bulk.bat --area Utility
```

## Tests

No unit tests. Check a batch with `dotnet build godot\GUO\GUO.csproj` and re-measure with `tools/port_audit`.
