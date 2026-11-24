# New-player gate

One command and one exit code for "a new player can play this build".

```
launchers\dev\newplayer_gate.bat            run the chain (needs a shard that creates accounts on first login)
launchers\dev\newplayer_gate.bat --list     print the chain
launchers\dev\newplayer_gate.bat --record   also keep the video (needs ffmpeg on PATH)
launchers\dev\newplayer_gate.bat --client-exe PATH\GUO.exe   run the chain on an exported client
```

The gate invents an account nobody has used, then runs these scenarios in order with the scenario runner
(`tools/scenario_run`, data_formats section 34). Each is its own client launch; all share one client home made for
the run (the runner's `--home`) and removed afterwards, so the first link is still a new player's.

| Link | Scenario | What a pass means |
|---|---|---|
| `create` | `client.newplayer.create` | A first login on an empty account opens character creation; a name, a ready-made profession and a city put the character in the world; the backpack opens and holds items; a held arrow key walks the character; it speaks; Log Out returns to the login screen |
| `return` | `client.newplayer.return` | The same account logs in again, the character is on the list by name, enters the world again and logs out |
| `options` | `client.newplayer.options` | Two options ticked in the Options gump and Okay pressed are in the saved profile (the gate reads it) |
| `kept` | `client.newplayer.kept` | A third start on the same client home: the paperdoll left open at log-out is open again without a key press, and the options are still saved |
| `equip` | `client.newplayer.equip` | A worn item dragged off the paperdoll lands in the backpack and is no longer worn; dragged back onto the paperdoll it is worn again (the same item, by serial) |
| `combat` | `client.newplayer.combat` | Tab puts the character at war; a double-click on the fixture's attack dummy makes it the client's attack target, the shard says the blow landed (`GUO_PAD: hit attack-dummy` in the journal) and the dummy's hits drop; Tab again is peace |
| `reconnect` | `client.newplayer.reconnect` | The game connection is dropped (`net.drop`); the client says the connection was lost, Okay returns to the login screen, the same account logs in again and is back in the world |

The chain stops at the first link that fails and exits with the runner's code for it (0 pass, 1 a step failed,
2 a missing prerequisite). Exit 3 is the gate's own refusal: less than 10 GB of memory free, nothing started.
Run folders land where the scenario runner puts them, with stills, events and a summary per link.

## The combat fixture

Before the `combat` link the gate runs `client.newplayer.fixture` on a game master lane account
(`UO_SHARD_GM_ACCOUNTS[1]`, `guohighlight` by default; `--fixture-account NAME` to choose another, `--no-fixture`
to skip). Its first character goes to the New Haven start (Trammel 3503,2574,14) and types `[GuoPadDummies`
(ModernUO patch 0004): a stationary, harmless attack dummy one tile east of the start, with the other pad dummies,
any earlier set cleared. That client logs out and exits before the `combat` link starts, so one client runs at a
time. The dummies stay on the dev shard between runs; a second run replaces them. A shard without patch 0004 fails
the fixture (exit 1, `place_dummies` step), which says so before any combat is tried.

The equip, combat, reconnect and fixture scenarios are written by `make_scenarios.py` from the `kept` link's
login steps; run it after changing those, and the self-test fails while they differ.

## What it does not cover

A pass here is a pass on the shard it ran against, not on other servers (`tools/server_lab` covers those).
The dropped connection is made on the client side (`guo_net_drop` shuts its socket); a shard that drops the
client is the same read path but is not exercised here. Combat is proven on a dummy that reports the blow;
a fight to the death, loot and the client's own death and resurrection are not covered.

## Notes

- The password is generated per run and passed in `GUO_SCENARIO_PASSWORD`; it is never printed or logged.
- `--only return --account NAME` re-runs the second link on an account from an earlier run; put that account's
  password in `GUO_SCENARIO_PASSWORD` first.
- `client.login.basic` is a different check: it needs an account that already has a character and staff
  rights (it uses `[go`). Do not run it on a fresh account.
- Self-test: `python -m pytest tools/newplayer_gate` (no client, no shard).
