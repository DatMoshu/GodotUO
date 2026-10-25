# network_tests

Checks for the client's packet parse loop that run without the engine.

The linked production `PerfStages` also checks disabled scopes, gate transitions,
exception unwinding, nested inclusive totals and reset/export behavior. These
checks do not exercise live socket/plugin dispatch or measure observer overhead.

The Debug-only `DiagnosticsFunctionalProbe.tscn` exercises production TCP,
Huffman decode, packet framing and plugin dispatch with counters off and on.
It sends fragmented synthetic traffic over a temporary loopback port, checks
callback mutation/veto/injection order, queued sends and reconnect/reset.
It requires no shard, account or client data and performs no FPS sampling:

```
godot-console --headless --path godot/GUO res://addons/guo_editor/Tests/DiagnosticsFunctionalProbe.tscn
```

The full smoke launcher includes this probe. Editor smoke additionally checks
repeatable movement queries against installed terrain and temporary custom-house
walls, preview/internal flags, customization bounds and scratch-buffer cleanup.

```
dotnet run --project tools/network_tests
```

`PacketLengthGuard` (godot/GUO/src/Network) drops a dynamic packet whose declared
length is under 3, the size of its own header, and consumes that header so
`PacketHandlers.ParsePackets` moves on. Upstream ClassicUO spins forever on a
length of 0. `Program.cs` drives a copy of the parse loop over short, malformed,
split and wrap-around headers, and fails if the loop spins.

`ServerIdentity` (godot/GUO/src/Network, docs/data_formats.md section 43) is checked the same way: the dotted client
version, finding the server's refusal among the newest journal lines (ModernUO, RunUO and ServUO wording), the plain
sentence added to a disconnect, and that neither the message nor the log line ever holds the server's address.
