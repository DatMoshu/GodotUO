# client.weather — shipping UO+ weather

Layer L1, non-parity, independently off by default in **Video → UO+ effects...**.
No extra dependency. Normal Debug and ExportRelease compile the effects; Labs
is only a probe harness. Smoke runs `src/Render/Weather/WeatherProbe.tscn`.

1. Use a scratch GM profile on the controlled dev shard. At Britain bank,
   capture all weather toggles off, then enable rain rings, puddles and wet
   player with individual densities 100. Cobblestone ground is selected by
   tiledata name; no game data or tile lists ship with the client.
2. Trigger a server storm using the shard's installed GM weather command.
   Record the exact command and packet type/count in the evidence receipt.
   Do not substitute the Labs rain override for this acceptance run.
3. Over 30 seconds, exposed flat cobblestone fills with puddles; within four
   seconds the dressed player's alpha contour receives wet drops. Near the
   docks, water rings respect pier/roof cover and foreground draw order.
4. Change storm intensity without changing its packet type. Half intensity
   produces half the impact rate and half the filling/wetting speed. Snow
   produces no new rain impacts, puddle fill or wetness. Frost is not provided.
5. Clear server weather. Rings expire within 0.65 seconds, body drops fade
   over 18 seconds and full puddles drain over 45 seconds. Capture dry, filling,
   wet and drained frames and a 15-second edited clip of the real progression;
   label any time compression. Include a measured frame-cost line.
6. Repeat surface checks on the Yew road: wet statics above sloped dry land
   remain eligible; unlisted/sloped dry tiles remain dry. A roof or raised
   surface covers the cell. Object chains exceeding 64 entries are skipped.
7. Density zero hides only that effect; disabling puddles removes them at
   once. Disable all and compare matched native-art frames for off-state
   parity. Change map/relog and check transient state clears.

Store all captures privately in the shared evidence run. Mark live checks
NOT RUN until exercised. Sitting, hidden and dead players have no wet outline.
