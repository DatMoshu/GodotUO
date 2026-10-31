# Offline art-capacity contract proof

Run `launchers/dev/art_capacity.bat doctor` and `launchers/dev/art_capacity.bat`
(shell twin available), or `python tools/art_capacity/run.py doctor|test`.
Requires the existing Python tool runtime and a .NET SDK supporting net8.0.
Build outputs use `Config.build/art-capacity-tests`, outside the checkout when
`UO_BUILD_DIR` is configured as required by AGENTS.md. No packages are fetched by
this tool beyond the SDK's normal target-framework resolution.

The console harness links the exact L4 `CapacityContract.cs`; it also links the
unchanged production packet table and client-version calibration to test golden
legacy lengths. Only optional mod lookup and logging are stubbed. It never reads
client data, writes an installation, opens sockets or runs Godot.

Layer: L4 offline candidate, explicit harness opt-in. Default and export clients
exclude Labs. See `docs/architecture/ADR-0047-bounded-art-capacity.md` (Proposed),
`docs/art_capacity_audit.md`, and `tools/scenarios/art-capacity/README.md`.
No runtime expanded-art or end-to-end interoperability claim is made.
