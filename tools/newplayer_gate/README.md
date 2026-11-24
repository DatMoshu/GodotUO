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
| `kept` | `client.newplayer.kept` | A third start on the same client home: the paperdoll left open at log-out is open again without a key press. **Written, not yet run against a shard** |

The chain stops at the first link that fails and exits with the runner's code for it (0 pass, 1 a step failed,
2 a missing prerequisite). Exit 3 is the gate's own refusal: less than 10 GB of memory free, nothing started.
Run folders land where the scenario runner puts them, with stills, events and a summary per link.

## What it does not cover yet

Dragging an item, equip and unequip, combat and a dropped connection.
The runner can hold a key and drag between controls (`ui.hold`, `ui.drag`); it has no step for a dropped connection yet.
A pass here is a pass on the shard it ran against, not on other servers (`tools/server_lab` covers those).

## Notes

- The password is generated per run and passed in `GUO_SCENARIO_PASSWORD`; it is never printed or logged.
- `--only return --account NAME` re-runs the second link on an account from an earlier run; put that account's
  password in `GUO_SCENARIO_PASSWORD` first.
- `client.login.basic` is a different check: it needs an account that already has a character and staff
  rights (it uses `[go`). Do not run it on a fresh account.
- Self-test: `python -m pytest tools/newplayer_gate` (no client, no shard).
