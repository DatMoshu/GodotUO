# Art capacity contract lab

L4, offline, default off. Status: candidate contract proof; no live client support.
The owner-authorized experiment tests whether explicit formats and bounded IDs
can reject malformed or incompatible content before state changes. The Proposed
[ADR](../../../../../docs/architecture/ADR-0047-bounded-art-capacity.md) owns the
design; it does not authorize a core/network change.

`CapacityContract.cs` only compiles in an opted-in Debug `GuoLabs=true` client.
The standalone synthetic harness explicitly defines `GUO_LABS`. No command,
Options action, socket, packet handler, archive loader or runtime feature calls
this code. There is no registration surface to bind. Normal and export builds
exclude the source through the existing Labs compile boundary.

Run `launchers/dev/art_capacity.bat doctor`, then `launchers/dev/art_capacity.bat`.
The shell twin works on platforms with a .NET SDK supporting net8.0. Missing
dependencies name the doctor launcher. No Godot, UO installation, server, Python
packages, GPU, external service or downloaded test assets are needed. Python is
the existing Tools launcher, never called from the client.

The harness exercises 9-byte art-only statics (signed-byte Z), 14/18-byte multis
(signed-short Z), positional tiledata counts, sparse art indices, a synthetic
offer/ack session and length-delimited equipment frame, and a bounded CPU cache.
This does not widen any production type, draw a high-ID sprite, prove picking,
test GPU eviction, or establish server/editor interoperability. See
`tools/scenarios/art-capacity/README.md` for repeatable steps and remaining gates.
