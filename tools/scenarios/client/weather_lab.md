# client.weather.lab — UO+ weather

Requires a Debug `GuoLabs=true` build. Contract smoke has no shard/install needs:

```text
godot-console --headless --path godot/GUO --quit-after 60 res://src/Labs/Weather/WeatherProbe.tscn
```

Expected log: `[weather] PASS`. The synthetic surface scene
`res://src/Labs/Weather/WeatherVisualProbe.tscn` renders dry, filling, full/raining
and water/raining tiles through the production surface drawing method. It uses
only procedural colours. Save its PNG using `GUO_WEATHER_PROOF` under the evidence
run. This is separate from the following live acceptance procedure.

1. Log into the dev shard with a scratch profile. Capture a frame with all three
   weather checkboxes off. Compare with a default build at the same position.
2. Stand by visible Wet land near the Britain docks. Trigger rain through the
   shard's weather control. Enable only water rings: expanding rings and short
   splashes appear on water, with foreground pier objects covering them.
3. Open `-uopweather`. Enable puddles and include cobblestone names or enter the
   current shard's land graphic IDs. After 15 seconds patches are partly filled;
   after 30 seconds full patches have splash/ripple responses to rain.
4. Verify unlisted dry tiles, sloping dry terrain and covered cells stay dry.
   Disable puddles: patches disappear immediately. Re-enable to begin filling.
5. Enable wet player. After four seconds, drops trace the current dressed body
   contour. Walk and turn; drops follow animation alpha, never a bounding box.
   Foreground objects cover the player and drops together. Hidden/dead/sitting
   players have no outline.
6. Reduce the budget to 2 and then 0. New surface impacts and body drops respect
   the budget. Existing rings finish. Set 12 to restore the default budget.
7. Stop rain: no new surface impacts; body wetness fades over 18 seconds and
   puddles drain over 45 seconds. Snow must not fill puddles or wet the player.
8. Change facet or relog: transient effect state clears. Disable all toggles and
   verify no new weather overlays or changes to selection, movement and UI.

Save captures and logs under the central GUO evidence run; never in the checkout.
Report live steps as NOT RUN until actually exercised. Sitting-body effects
are not acceptance targets for this initial Labs version.
