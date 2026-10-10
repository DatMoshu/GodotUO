# tools/editor_objects_proof

Proves a world project's world objects (spawners and decoration, ADR-0014)
reach a game client, on the **private** ModernUO instance
(`tools\editor_shard`, 127.0.0.1:2594). It never touches the shared dev
shard.

```
python tools\editor_objects_proof\run.py --project DIR [--out DIR] [--headless]
```

Every mode empties `--out` before writing, so `--out` must be a folder
inside the repo's `build\` folder (not `build\` itself) and must not overlap
the client install; anything else is refused before the tool touches a file
or the shard. `--clip` must likewise name a file inside `build\`.
`python tools\editor_objects_proof\test_run.py` checks both guards.

1. `tools\world export` and `verify` turn the project's `shard\objects.json`
   into ModernUO files plus the manifest (`docs\data_formats.md` §13).
2. `tools\editor_shard`: stop, install the bridge, then
   `start --objects <export>`. After the world loads, the bridge syncs and
   logs `world objects synced: spawners +a ~c -d =k, items ...`.
3. A GUO client logs in on the probe account, goes next to the first object,
   and writes every item and mobile within 12 tiles (`--objects-dump`). Then
   it captures a frame. Its window is shown without activation and never
   takes the keyboard. `--headless` has no window and no frame; the client
   is ended once the dump is written.
4. **Checks against the model:**
   - every placed item stands in the client's world at its cell, with its
     art and hue;
   - every spawner's item stands at its cell;
   - the creatures near each spawner are listed.

## Live mode

```
python tools\editor_objects_proof\run.py --live [--headless]
```

The live tier (ADR-0014), with no export and no restart:

1. The shard starts with the bridge, and with GUO's earlier objects removed
   (`tools\editor_shard start --clear-objects`).
2. A client logs in, stands at 1164,1668 and stays. `--objects-watch` makes
   it answer dump and frame requests.
3. A headless editor, live on the bridge (`--guo-editor-live objects`),
   places an anvil and a Horse spawner through the World tab's object layer.
   It then moves the anvil, then deletes both. Each step waits for the
   shard's acknowledgements.
4. The client's world is looked at before and after each step. The objects
   must appear, move and go.

Measured 2026-09-27: 8/8 checks. The shard acknowledged each put or delete
in 0-16 ms, and the frames show the anvil and the horse appear, the anvil
move, and everything go.

## Commands mode (a shard without GUO's bridge)

```
python tools\editor_objects_proof
un.py --commands --project DIR --project2 DIR2
```

1. One boot with the bridge and an empty manifest clears what earlier bridge
   runs placed. Then the shard starts with the bridge **unlisted**
   (`tools\editor_shard start --no-bridge`): a plain ModernUO.
2. `tools\world apply-commands` places `--project` through a GM client.
3. A GM places an untagged **decoy** anvil on the project anvil's cell: shard
   content that the fallback must never remove.
4. `--project2` (the same project edited, with its record of what the
   fallback placed) is applied.
5. Checks:
   - the project's anvil moved, still tagged;
   - its spawner is gone;
   - the hued item stands;
   - the decoy is the only thing left on the old cell;
   - applying `--project2` again types nothing.

## ServUO

```
python tools\editor_objects_proofun.py --servuo --project DIR
python tools\editor_objects_proofun.py --commands --servuo --project DIR --project2 DIR2
```

`--servuo` exports with the ServUO backend (XmlSpawner XML + decoration cfg),
verifies, puts the files beside the private ServUO (`tools\servuo`,
127.0.0.1:2596), restarts it, and has a GM client type `[XmlLoad` and
`[Decorate`. The client must then see each item at its cell, and each
spawner with its creature nearby. `--commands --servuo` runs the GM-command
fallback's checks, decoy included, against ServUO instead of the private
ModernUO.

## Clip

`--live --clip FILE.mp4` adds a recorded pass after the live checks pass:

- the client (windowed, never focused) records 16 s at 10 frames a second
  (`<name>.rec` in its watch folder) while the editor puts, moves and
  deletes;
- ffmpeg writes an H.264 MP4 with a caption for each step.

Output: `build\editor_objects_proof\`, holding `report.json`,
`client_objects.json`, `client.png` and the logs. It contains renders of
client art, so it is never committed.

## Measured 2026-09-27

On a fresh private instance:

| Run | Shard sync | Client |
|---|---|---|
| Project with an anvil and a Horse spawner | `spawners +1, items +1` | The anvil at its cell, the spawner item, and a horse beside it; all three in the frame |
| Same project, anvil moved, spawner deleted, hued item added | `spawners -1, items +1 ~1` | One anvil at the new cell, the new item with its hue, no spawner, no horse |
| Same export again (`--headless`) | `+0 ~0 -0 =2` (nothing written) | Unchanged |
