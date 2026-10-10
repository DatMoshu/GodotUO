# tools/plugin_probe

Loads two test assistants into GUO and checks what they saw: a native DLL
(`native/probe.c`, ClassicUO's plugin ABI) and a managed .NET Framework
assembly shaped like Razor (`managed/`, loaded through `tools/plugin_host`).
Each must be installed, initialised, told of the connection, see packets both
ways and get the player's position right; the managed one also injects pings
the host must drop without ending the session. The client's home is
`build/plugin_probe/home`. Needs a shard.

## Run

```
launchers\dev\plugin_probe.bat
```

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed. Exit 0 when both plugins pass.
