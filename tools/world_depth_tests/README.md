# World depth ordering checks

The paired `launchers/dev/smoke` scripts run this suite and send generated
MSBuild output to `UO_BUILD_DIR/world-depth-tests`. To run it independently,
use `dotnet run --project tools/world_depth_tests/WorldDepthTests.csproj
--artifacts-path <external-build-directory>` (replace the placeholder).
This links the production comparator and CPU key planner without Godot, a shard,
client data or a runtime lease.

The suite compares complete submission order, including page/blend segment
boundaries, cross-diagonal ties, chunk traversal, more than 16 tile entries,
dynamic insertion/removal, and packing overflow. It does not prove rendered
parity, blending, picking, persistent-buffer invalidation or performance.
The planner is preparation for GPU01; only its unchanged comparator is connected
to RenderLists. GPU-world rendering remains unimplemented and off by default.
