# client.water.rain — rain impacts in styled-water simulation

Requires a Debug `GuoLabs=true` build. Contract and synthetic GPU commands are
in [the rain doctor](../../../godot/GUO/src/Labs/WaterRain/README.md).
Use `WeatherProbe.tscn` too to check the shared normalized rain input.

1. With every weather toggle off, compare styled water against the same scene
   before this feature; no rain stamps or primitive overlays should appear.
2. At exposed Wet land and static water near Britain docks, choose a styled
   mode and enable only **Rain rings on water** in `-uopweather`. Trigger rain
   through the controlled shard's weather tools. New disturbances spread through
   the water field; its existing style renders them. Verify all five styles.
3. Change rain intensity from low to maximum: candidate impact frequency follows
   intensity. Set the weather drop budget to zero: no new rain impacts occur.
   Restore 12; each frame still has an eight-attempt ceiling.
4. Check dry land, unloaded edges, roofs, bridges/decks and a higher ground plane
   over water: rain must not disturb the covered/lower water. Existing foreground
   sorting and picking must remain intact. Move to another water height/facet.
5. Toggle from Classic's opted-in primitive rings to a styled mode: no duplicate
   primitive rings should remain. Return to Classic: primitive fallback works.
   Default-off Classic must retain native output. Disable the checkbox with a
   styled mode: only pre-existing waves continue calming.
6. Stop rain or switch to snow: no new water-rain stamps. Walk from dry land onto
   water, teleport, change facet and relog: no accumulated rain burst.
7. Check puddles and wet-player toggles independently; styled-water suppression
   must affect only water-ring primitives.

Record live steps as NOT RUN until exercised against a controlled shard.
Synthetic fields and pixel comparisons prove the wave/shader contract, not
in-world placement, roof occlusion, live appearance or frame-time cost.
