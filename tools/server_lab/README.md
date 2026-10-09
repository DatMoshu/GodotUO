# Server compatibility lab

Runs GUO's scripted cases against UO server emulators, backend by backend, and keeps the grid of results
(backend x case). The pins, licences and per-server needs are in [docs/server_lab.md](../../docs/server_lab.md); the
results page is [docs/wiki/Server-Compatibility.md](../../docs/wiki/Server-Compatibility.md), which this tool writes.

```
python tools\server_lab\run.py doctor [BACKEND]          toolchains, disk, UO data, each row's state
python tools\server_lab\run.py setup BACKEND             fetch at the pin, patch, build, configure, admin, profile
python tools\server_lab\run.py start | stop | status BACKEND
python tools\server_lab\run.py seed BACKEND              give the lab account its character (once per world)
python tools\server_lab\run.py cases BACKEND [--case 0,1,9] [--no-record]
python tools\server_lab\run.py row BACKEND [--no-record] [--reseed]   all of it, then stop, wiki, card
python tools\server_lab\run.py wiki                      rewrite the wiki page from the grid
python tools\server_lab\run.py card BACKEND              build/server_lab/card_<backend>.json (sends nothing)
```

`launchers\dev\server_lab.bat` runs the same. Backends today: `modernuo` (SV1). ServUO, UOX3 and Sphere X arrive
with SV4, SV5 and SV6; their rows read "not run" until then.

## What each step does

| Step | ModernUO row |
|---|---|
| setup | Clones ModernUO into the lab profile's own folder in the workspace (`servers/<id>/src`, not the dev shard's `tools/modernuo/src`), checks out the `lab` pin from `tools/server_manager/backends.json`, applies `tools/modernuo/patches`, builds with ModernUO's `publish.cmd`, writes `modernuo.json` and `expansion.json` from `tools/modernuo/config` on `127.0.0.1:2610`, generates the lab admin and saves the profile **ModernUO (server lab)** in the workspace's `servers.json` |
| start / stop | Through `tools/server_manager` (`start_server` / `stop_server`), which keeps the editor run bar's own process record: the run bar shows the lab server and can stop it, and the other way round. The listener is put back to loopback at every start |
| seed | One `launchers\game\play.bat --play` login as the lab admin on a scratch profile: the client's probe login makes the character, then `[save` keeps it through the lab's forced stop |
| cases | Each case of `cases.json` that has a scenario, through `tools/scenario_run/run.py <scenario> --server <profile>`; the login goes to the runner in `GUO_SCENARIO_ACCOUNT` / `GUO_SCENARIO_PASSWORD`, never on a command line |
| row | setup when missing, start, seed when not yet seeded, cases, stop (only a server it started), wiki, card |

## Safety

- **Loopback only.** Every lab server listens on 127.0.0.1. ModernUO's UDP ping listener also moves off its shared
  default (12000) to the game port plus 10000 (12610), so the lab row and the dev shard can run side by side.
- **Its own admin.** The lab account (`guolab` on ModernUO, `table.json`) and its password are generated into the
  per-user workspace, `server_lab/<backend>/secrets.json`; nothing ships a password and the dev shard's owner and
  game master accounts are never used or created. Passwords are 16 letters and digits: the classic login gump's
  password box holds 16 characters (upstream `LoginGump.cs`), and a longer one is cut short as it is typed.
- **Only what it started.** stop ends the recorded process tree only when its PID, start time and executable all
  still match.
- **Leases.** Take `build:D` for setup and `shard:<port>` plus `godot:runtime` for start, seed and cases
  (switchboard). The tool itself takes none.

## Files

| File | Holds |
|---|---|
| `cases.json` | The 25 cases of the lab plan plus case 0 (the client reaches its login screen, no server); `scenario` is null until the case is written (SV2a/SV2b) |
| `table.json` | Per backend: lab profile name, shard name, account and character, and `vars`, the words that differ between servers (staff commands, places), for scenarios that take them as `--var` |
| `triage.json` | Optional (SV7): `{"<backend>": {"<case>": {"verdict": "guo" | "server-gap" | "n/a", "why": "..."}}}`. A verdict turns "FAIL (untriaged)" into its owner's failure; `n/a` marks a case the server does not have |
| `build/server_lab/grid.json` | The grid, data_formats section 36 (gitignored) |

## Tests

`python tools\server_lab\test_server_lab.py`: the tables, grid, triage, wiki page, card, the ModernUO configuration
and the server manager's process record (a real short-lived process). Nothing is fetched or started.
