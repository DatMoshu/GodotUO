# tools/editor_shard

A **private** ModernUO instance for the editor's live and export work, so
nothing the editor does touches the shared dev shard (`launchers\shard`),
which other agents and devices play on.

```
python tools\editor_shard\run.py setup   [--from DIR] [--port 2594]
python tools\editor_shard\run.py start   [--data-first DIR]
python tools\editor_shard\run.py status
python tools\editor_shard\run.py stop
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
- **bridge** builds `bridge\GUO.EditorBridge.dll` (a ModernUO assembly, ADR-0012)
  against the copy's own `Server.dll` and lists it in the copy's
  `Data\assemblies.json`. It is never installed in the shared shard. Once
  running it offers the editor bridge on `127.0.0.1:2595` (JSON lines,
  `docs\data_formats.md` section 10), and makes game clients UltimaLive
  clients of shard `GUO-Editor-Private` for map 0. Stop the instance first;
  its `Assemblies` are locked while it runs.
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
