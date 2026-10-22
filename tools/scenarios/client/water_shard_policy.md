# client.water.shard-policy — WATER-05

Layer L1 / UO+, default off. Existing content descriptor transport; no doctor
dependency beyond the normal client/shard and a deployed content descriptor.
Contract smoke: `WaterProbe.tscn` tests consent persistence, revocation,
restriction changes, missing packs and endpoint isolation. Admin Settings and
descriptor parsing/deployment tests run without Godot in server_manager and
shard_content respectively.

Live acceptance, using a throwaway shard profile and an installed style:

1. Deploy shard content and set the server profile's content URL to its existing
   public descriptor. Admin > Settings > UO+ water rendering: default `uo`,
   restrictions on, allowed empty. Save, reload and verify the same values.
2. Play with that content. At Britain docks, personal water Classic remains
   Classic. Open Options > UO+ effects: "Water set by shard" and unchecked
   consent are visible. Capture the panel and water.
3. Check consent then Cancel: Classic remains. Reopen, check then Apply: `uo`
   renders. Reopen: water selector offers only `uo`. Capture panel and water.
4. Uncheck consent then Apply: the original personal style returns. Repeat
   with a non-Classic personal style to verify it was not overwritten.
5. Turn restrictions off in Admin Settings, reconnect through the descriptor
   and verify consent is unchecked. Accept, reopen and select Classic. Apply:
   the player's override is honored. Relog: consent and override persist.
6. Change the default to an unavailable pack style with restrictions on.
   Reconnect and consent: Classic fallback, no automatic download. Withdraw
   consent; the personal setting returns. Connect to another endpoint: the
   previous shard's policy must not apply.

Save all panel/world stills and logs to the seat's central evidence run. Do
not mark live acceptance complete from the contract probes alone.
