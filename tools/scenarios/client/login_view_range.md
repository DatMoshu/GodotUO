# Login population before the view-range reply

L0 correctness repair in `World`, shared by canvas and Depth World. No option,
wire-format change, server change, automatic range increase, or new packet logs.

## Failure

A modern server may populate the world before replying to the client's `0xC8`
range request. With an extended server range, World.Update used the provisional
24-tile default to permanently remove entities that had just arrived. The
subsequent range reply did not recreate them. `0x77` updates intentionally ignore
unknown mobiles, so walking closer could leave the hole until a fresh population.
This was reproduced with a vendor and door as well as bull/wolf body IDs.

## Behavior

- Before a known range, modern-client local distance pruning waits. Updates,
  animations, explicit server deletes and world teardown continue normally.
- The existing range setter (server reply or explicit range macro) enables
  pruning with exactly that byte value. No clamping or 72-tile assumption.
- Older clients, before CV_305D, retain default-range pruning; they do not send
  the view-range request in LoginComplete.
- World.Clear resets the provisional range and readiness for a new connection.
  Map changes within a connection keep the negotiated range.
- A modern shard that never answers the request remains responsible for sending
  delete-object packets. There is deliberately no guessed timeout/range. Such a
  shard may retain more client entities until explicit deletes or logout; an
  explicit range macro restores local pruning. This is a compatibility limit to
  check against other shard backends, not a claim of coverage for every shard.

## Focused fixture

`login_view_range.cs.fixture` runs actual World.Update against a separate World
after normal client data/profile initialization. It is injected into an external
diagnostic build, never included in shipping source. Its synthetic serials belong
only to that World. The player remains a distance anchor but is not updated as a
second input loop. RangeSize matches the anchor, as in EnterWorld.

Checks: initial mobile and ground item beyond 24 survive; negotiated72 keeps
them; inclusive72 and exclusive73 boundaries; reduction24; reset/relogin; explicit
server-removal methods while pending; full-byte255 boundary; legacy readiness.
The current-version real update is exercised, while the legacy policy branch is
checked directly. No synthetic network packets are sent by the fixture.

## Functional confirmation

At the same player position and shard, preserve old and new executable/source
bindings. Record entity creation, destruction caller and the range reply without
logging auth packets. Confirm received objects survive through reply and normal
movement. Compare serials and world presence before diagnosing renderer culling;
two clients may receive different live positions at different instants.

Evidence: shared run `20261027_guo-lead_high-fps-polish`, range-red-corrected
binding and `movement-human-range-red2` versus range-green binding and
`movement-human-range-green`. These are functional diagnostics, not FPS samples.
