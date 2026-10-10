# tools/editor_shard

A **private** ModernUO instance for the editor's live and export work, so
nothing the editor does touches the shared dev shard (`launchers\shard`),
which other agents and devices play on.

```
python tools\editor_shard\run.py setup   [--from DIR] [--port 2594]
python tools\editor_shard\run.py start   [--data-first DIR] [--objects EXPORT | --clear-objects]
python tools\editor_shard\run.py status
python tools\editor_shard\run.py stop
python tools\editor_shard\run.py admin-check [--no-client]
python tools\editor_shard\run.py admin-tab [--windowed]
```

- **setup** copies the built ModernUO `Distribution` (the same
  `tools\modernuo` build; the main worktree's when this checkout has none)
  into `build\shard_private`. It leaves out `Archives`, `Backups`, `Logs`
  and `temp` (about 55 MB instead of 2.4 GB). It then rewrites the copy's
  `Configuration\modernuo.json`: listener `127.0.0.1:<port>`, and nothing
  wider. The shared shard's files are only read. The copied `Saves` carry
  the dev shard's accounts, so the GM lane accounts log in here too.
- **start** runs the copy in the background (log: `build\shard_private\shard.log`)
  and waits until it listens. `--data-first DIR` puts a `tools\world` export
  ahead of the install in `dataDirectories` for this start; a start without
  it puts the install back alone.
- **start --objects EXPORT** applies a `tools\world` export's world objects
  (ADR-0014). It copies the export's `shard\Data\` into the copy's `Data\`
  and the manifest to `Data\GUO\guo_objects.json`. After the world loads,
  the bridge syncs it: spawners by GUID through ModernUO's own `SpawnerDto`
  path, items by the serial it recorded. It adds, changes, moves and
  deletes; only what it created is deleted. It then saves the world.
  - GUO's record lives in the save (`GUOWorldObjects`).
  - A later start without `--objects` syncs the last manifest again, which
    changes nothing.
- **start --clear-objects** installs an empty world-objects manifest. The
  boot sync then removes every object GUO placed, and nothing else.
- **bridge** builds `bridge\GUO.EditorBridge.dll` (a ModernUO assembly, ADR-0012)
  against the copy's own `Server.dll` and lists it in the copy's
  `Data\assemblies.json`. It is never installed in the shared shard. Once
  running it offers the editor bridge on `127.0.0.1:2595` (JSON lines,
  `docs\data_formats.md` section 10), and makes game clients UltimaLive
  clients of shard `GUO-Editor-Private` for map 0. Stop the instance first;
  its `Assemblies` are locked while it runs.
- **admin channel** (ADR-0035): **start** hands the shard the user's bridge
  admin token (`UO_BRIDGE_ADMIN_TOKEN`, generated into the workspace's
  `shard\secrets.bat` if missing). Admin ops on the bridge need it in the
  editor's `hello`; map editing does not. Every admin op is audited in
  `build\shard_private\Logs\GUO\admin_audit.jsonl`, secrets masked.
- **admin-check** talks to the running instance's bridge and checks the admin
  channel: no token, a wrong one, the right one, the audit log, the close
  after three refusals, the god view (a whole facet, then change-only pushes
  as a spawner is put and deleted, Find by name and serial), the god view's
  actions (AD2b), the Settings form's `admin_settings` (AD4: live values, a
  secret one as `***`; a change audited with a webhook masked), the Accounts
  ops (AD5, `admin_accounts_check.py`: an account made with a 16-character
  password, given a level and a typed password, banned and unbanned, each
  proved by a login on the shard's login server; refusals; passwords masked
  in the audit log; the account, `ad5c` and six digits, stays on the
  instance) and that no token or password reached a log. For the actions
  it puts two test spawners west of Britain and logs a headless GUO client in
  as the third game master lane account (`UO_SHARD_GM_ACCOUNTS`, its
  character named after the account; `staff_client.py`), then checks Go
  there, Bring here and Open paperdoll in that client's objects dump, Follow
  (it takes the character back after a Go there away, and ends by itself
  when the target is deleted), Respawn and Clear, and every refusal. Its
  spawners are deleted at the end. `--no-client` checks the spawner actions
  and refusals only. **start** passes `UO_SHARD_GM_ACCOUNTS` so those
  accounts get the generated password. The same
  channel without ModernUO:
  `dotnet run --project tools\editor_shard\bridge\tests\AdminChannel.Tests.csproj`.
- **admin-tab** drives the GUO editor's Admin tab against this instance, in a
  scratch workspace (`build\admin_tab\workspace`): the run bar starts the
  instance with the admin token, the tab connects and reads Health, Save now
  saves, Restart saves and has the run bar stop and start the server, and the
  tab reconnects; the god view watches Felucca again, shows a spawner put
  through the bridge with its horses, finds it, hides NPCs with the filter,
  presses its Respawn and Clear buttons (AD2b) and drops it on delete. Then
  the Settings form (AD4) reads the shard's configuration, refuses values out
  of range, and Save and restart writes two settings and a test mail password;
  the restarted server reports the new values, the previous files are kept
  without the password, and the shard's `Configuration` folder (copied aside
  first) is put back as it was. Then the Accounts list (AD5) shows every
  account, keeps the owner out of reach, makes an account (`ad5t` and six
  digits, left on the instance) with a generated password, gives it a level
  and a typed password, bans it and lifts the ban. Then it checks that every time in the tab's log is UTC with a Z, and that no
  token or password reached the editor output, the tab's log, the server
  console or the audit log. It stops a running `start` first. `--windowed` saves stills of the tab and a clip under
  `build\admin_tab`.
- **stop** ends only the process `start` recorded, and only if its executable
  is the copy's. It cannot stop the shared shard.

Clients reach it with environment variables for that run only
(`UO_SHARD_HOST=127.0.0.1`, `UO_SHARD_PORT=2594`); `config.bat` is not changed.

## The export proof, on this instance (2026-09-27)

The world project `build\shard_proof_project` replaces block map0 145,208, a
wilderness block west of Britain. It adds a crate and two trees, and raises a
3x3 patch of land (cells 1161-1163 x 1665-1667) to z 20.
`tools\world\run.py export` wrote it to `build\shard_proof_export`, and
`verify` passed.

`[go` places a character at the height the **server** computes from its own
map, and `[where` reports it:

| Private shard started with | `[go 1162 1666` then `[where` |
|---|---|
| the install only | `You are at 1162 1666 0 in Felucca.` |
| `--data-first build\shard_proof_export` | `You are at 1162 1666 20 in Felucca.` |

So the shard reads the export.

## Repeating it on the shared dev shard (for the owner)

This restarts the shared shard, so run it only when nobody else is on it.
From your main checkout (not a worktree), with the export built as
above:

1. Back up `tools\modernuo\src\Distribution\Configuration\modernuo.json` and
   `...\Distribution\Saves`.
2. In game as a GM, type `[save` and wait for "World save completed".
3. Stop the shard (Ctrl-C in its console window).
4. Edit `modernuo.json`: set `dataDirectories` to
   `["D:\\_uo\\Godot\\wt_editor\\build\\shard_proof_export", "<your UO install>"]`.
5. `launchers\shard\run.bat`. `configure.py` only writes `modernuo.json` when
   it is missing, so the edit stands.
6. In game: `[go 1162 1666`, then `[where`. It should say z 20.
7. `[save`, stop the shard, restore the backed-up `modernuo.json`, and run
   `launchers\shard\run.bat` again. `[go 1162 1666` + `[where` should say z 0.
