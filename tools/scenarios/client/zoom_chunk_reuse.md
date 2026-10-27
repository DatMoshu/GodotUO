# Depth World zoom: chunk visibility reuse

Layer: existing L4 Depth World path (ADR-0044), optimized Debug with
`GuoLabs=true` and `--depth-world`. Ordinary canvas mode keeps the full walk.
No new option, input behavior, camera transform or texture sampling change.

Zoom changes the pixel window even when object screen positions do not move.
Reuse only tiles with every possible anchor inside **both** the previous and
current window, using each window's corresponding draw offset. Boundary,
dynamic and dirty tiles are walked. The zero-mask path must advance its view
record despite bypassing `EndChunkWalk`. Moving skipped tiles retain the stale
position bit until visited. Scene/roof/mesh/picking invalidations still apply;
circle transparency retains the former full walk when its window changes.

## Functional regression

`zoom_chunk_reuse.cs.fixture` is a diagnostic source fixture, excluded from
the product build. Use a disposable external source stage with isolated
`UO_BUILD_DIR`, `UO_CACHE_DIR` and `UO_WORKSPACE_DIR`; point engine, upstream
and client-data settings at their existing configured locations. Take a
project Godot slot. Do not use an account or log in to a shard.

1. Copy the fixture into the stage as
   `godot/GUO/src/Bootstrap/ZoomReuseFixture.cs`. In the stage only, add
   `AddChild(new ZoomReuseFixture());` immediately before the `bootArgs`
   declaration in `Main.StartClient`.
2. Build optimized Debug with `GuoLabs=true`; import the staged project.
   Set `GUO_ZOOM_FIXTURE_DIR` to an empty evidence directory,
   `GUO_DW_REUSE=1` and `GUO_DW_REUSE_VERIFY=0`. The zero-mask early return
   must run: the older in-loop verifier bypasses that return.
3. Run the configured `godot-console` against the stage using
   `--rendering-method forward_plus -- --play --depth-world --silent
   --no-focus --pregame-classic --window-size 1280x720`. No autologin.
   Routine runs use 720p; use 1080p when needed. A 4K repeat is only for an
   explicit large-viewport coverage check or owner request; verify the actual
   camera bounds in `renderer.json`.
4. Require `complete`, exit 0, no fixture failure, and active Depth World
   frames without fallback. Inspect the private captures at zoom 0.5, 1.2
   and 5. The fixture creates an offline player on the Britain map using
   the local install, without creating a server account or world entity.
5. Require all mask cases: 200 deterministic window/offset/Z-range pairs
   against actual object projection at every Z; dynamic/dirty bits;
   repeated zero walks and later edge transition; stale position tracking;
   mesh rebuild/content, roof limit, picking and circle-transparency guards.
   The deliberately wrong current-window-only calculation must be rejected.
6. Require Ctrl+wheel in/out, both range clamps and wheel without Ctrl.
   Require real zero and partial reuse during zoom changes. At each zoom
   (1, 1.1, 1.2, 1.1, 0.5, 2.5, 5, 2.5, 1) and after moving the player
   while zooming to 1.3, compare a reused gather with a forced whole walk
   in the same frame. Visibility, visible vertex data, lights and selected
   object must match. No world update, packet or second alpha step intervenes.
7. Remove the diagnostic fixture and startup hook from the stage; run the
   normal `launchers/dev/smoke.bat` before committing the source change.

These are correctness and branch-coverage checks, not frame-time evidence.
An offline fixture does not establish live mobile/render-order parity.
Newly exposed chunks still require data loading, mesh creation and uploads;
this change only preserves valid existing tile walks across view changes.
Performance measurement requires separate owner authorization.
