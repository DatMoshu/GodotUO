# Regression probe

From the repository root:

```powershell
dotnet build godot/GUO/GUO.csproj --no-restore
tools/godot/godot-console.cmd --headless --path godot/GUO res://dev/regression_probe.tscn
```

No UO installation or shard is needed. The process exits nonzero on failure.
Append `-- atlas`, `-- json`, `-- tcp`, `-- websocket`, or `-- video` to select one group.
Network tests use loopback connections with timeouts.

Checks cover atlas packing boundaries, dedicated texture pixel contents and
updates, normal page rollover, profile JSON round trips, idle TCP connections,
data followed by FIN, and fragmented WebSocket messages below, at, and above
the one-megabyte cap. Atlas pixel checks use the CPU images; headless mode
does not validate GPU rendering.

For the tree/roof ordering regression, run with a real renderer:

```powershell
tools/godot/godot-console.cmd --path godot/GUO res://dev/regression_probe.tscn -- rendering
```

This reproduces the old mesh-before-sprite overlap with synthetic art, then
reads GPU pixels to verify trees behind and in front of a roof, roof cutouts,
and the cached static's world offset. `all` includes this check when running
with a real renderer and explicitly skips it in headless mode.
