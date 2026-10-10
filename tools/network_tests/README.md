# network_tests

Checks for the client's packet parse loop that run without the engine.

```
dotnet run --project tools/network_tests
```

`PacketLengthGuard` (godot/GUO/src/Network) drops a dynamic packet whose declared
length is under 3, the size of its own header, and consumes that header so
`PacketHandlers.ParsePackets` moves on. Upstream ClassicUO spins forever on a
length of 0. `Program.cs` drives a copy of the parse loop over short, malformed,
split and wrap-around headers, and fails if the loop spins.
