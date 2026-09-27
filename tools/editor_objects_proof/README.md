# tools/editor_objects_proof

Proves a world project's world objects (spawners and decoration, ADR-0014)
reach a game client, on the **private** ModernUO instance
(`tools\editor_shard`, 127.0.0.1:2594). It never touches the shared dev
shard.

```
python tools\editor_objects_proof\run.py --project DIR [--out DIR] [--headless]
```

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
