# GUO divergences from ClassicUO

Ported files stay as close to upstream as they can (AGENTS.md rules 2 and 3).
When a ported file has to differ from ClassicUO for a reason other than the
port itself (a renamespace, a Compat or SDL using, a rewrite-tier
replacement), the edit is listed here, so that the next upstream merge can
reconcile it by hand instead of overwriting it.

Each entry names the ported file, the upstream file, what changed, why, and
the GUO code it calls. Edits in ported files carry a `// GUO:` comment.

| Story | Ported file | Upstream file | Change | Why |
|---|---|---|---|---|
| SF7 | `godot/GUO/src/Client/Main.cs`, `ReadSettingsFromArgs` | `src/ClassicUO.Client/Main.cs`, same method | The `ARG: {cmd}, VALUE: {value}` trace logs `ArgTrace.Value(cmd, value)` instead of `value`: the options `username`, `password` and `password_enc` show `<hidden>`, every other option its value. One line. | GUO's bootstrap passes `-username` and `-password` to the client for autologin (scripted runs, probes), so any log that kept trace lines carried the account and its password. |
| SF9 | `godot/GUO/src/Network/PacketHandlers.cs`, `ParsePackets` | `src/ClassicUO.Client/Network/PacketHandlers.cs`, same method | After `GetPacketInfo`, a call to `PacketLengthGuard.RejectShortDynamic(stream, offset, packetlength)`: a dynamic packet whose declared length is under 3 (its own id and length header) has those 3 bytes consumed, a warning logged, and the loop continues. The check lives in `godot/GUO/src/Network/PacketLengthGuard.cs` (GUO-owned). | Upstream dequeues the declared length and loops: a length of 0 dequeues nothing, so one hostile or corrupt packet spins `ParsePackets` forever and freezes the client; 1 or 2 points the handler past the end of its data. |
| SF9 | `godot/GUO/src/Network/PacketHandlers.cs`, `AnalyzePacket` | `src/ClassicUO.Client/Network/PacketHandlers.cs`, same method | The `bufferReader(world, ref buffer)` call is wrapped in `try`/`catch (Exception)`; the exception is logged with the packet id and length, and that one packet is dropped. | Upstream lets a handler's exception end the frame's whole parse loop, so one malformed packet loses every packet queued behind it, or crashes the client. |
| SF9 | `godot/GUO/src/Network/Socket/TcpSocketWrapper.cs`, `Connect` | `src/ClassicUO.Client/Network/Socket/TcpSocketWrapper.cs`, same method | `_socket.Connect(host, port)` becomes `ConnectAsync` plus a wait of at most `ConnectTimeout` (5 s, a new field). On timeout it logs, disposes the socket and raises `OnError(SocketError.TimedOut)`. A new `catch (AggregateException)` unwraps a `SocketException` from the task into the existing error path. The wait stays synchronous: callers check `IsConnected` right after. | Upstream's blocking connect holds the window for the OS timeout (about 21 s on Windows) when the host is dead or unreachable. |

## Reconciling on an upstream merge

- **SF7.** Keep the `ArgTrace.Value(cmd, value)` call on the `ARG:` trace line.
  If upstream adds a command-line option that carries a credential, add its
  name to `ArgTrace.Secret` in `godot/GUO/src/Client/ArgTrace.cs` (GUO-owned).
  `dotnet run --project tools\arg_trace_tests` checks both: it fails if the
  trace prints a raw value, or if an option whose name holds `pass`, `user`,
  `token` or `secret` is not hidden.

- **SF9, `ParsePackets`.** SF9's edits are marked `// PORT DEVIATION (GUO):`.
  Keep the `PacketLengthGuard.RejectShortDynamic` call between `GetPacketInfo`
  and the `stream.Length < packetlength` check. If upstream adds its own guard
  on short dynamic lengths, drop ours and `PacketLengthGuard.cs`. If upstream
  changes how `GetPacketInfo` reports the header (the offset of 3 for dynamic
  packets), update the guard to match. `dotnet run --project tools\network_tests`
  replays the parse loop over short, split and wrapped headers and fails if it
  spins or drops good packets; keep its copy of the loop in step with
  `ParsePackets`.

- **SF9, `AnalyzePacket`.** Keep the `try`/`catch` around the handler call.
  If upstream adds its own exception handling there, keep whichever logs and
  drops the one packet rather than ending the loop.

- **SF9, `TcpSocketWrapper.Connect`.** Keep `ConnectAsync` with the bounded
  wait, the timeout branch and the `AggregateException` catch. If upstream
  moves to an async or cancellable connect with its own timeout, take
  upstream's and drop ours, as long as a dead host still fails in seconds.
