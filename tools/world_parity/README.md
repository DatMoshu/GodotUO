# tools/world_parity

Compares the editor's UO World view (ADR-0015) with a **logged-in client's**
frame of the same cell, pixel by pixel. It's the check in phase 2 of
`docs/editor_plan.md`, and it also proves the export side of phase 3.

```
python tools\world_parity\run.py [--at X,Y] [--client-root DIR] [--season S]
                                 [--project DIR --files-override FILE] [--client-only]
```

## What it does

1. **Editor shot.** It opens the editor with
   `--guo-editor-world-shot 0,X,Y,600,480,<season>`. The World tab draws that
   cell in a viewport exactly the size of a fresh client's world view, with
   no editor guides, and saves `editor\world_shot.png`.
   - By default `UO_WORLD_PROJECT` points at an empty project, so only the
     install is drawn.
   - With `--project`, that world project is drawn over the install.
2. **Client shot.** It plays GUO from `--client-root` (default: this
   checkout) as the **last** account in that checkout's
   `UO_SHARD_GM_ACCOUNTS`, as `tools/multi_client` does (password = account,
   character = capitalised account).
   - It types `[go X Y` and photographs the frame.
   - The client's home is a scratch folder under the output, so no player's
     settings or profiles are touched.
   - A `default.json` profile there turns the top bar off.
   - With `--files-override`, the scratch `settings.json` points upstream's
     `files_override` at an export's `files_override.txt`, so the client reads
     the exported map files (tools/world).
3. **Compare.** The editor frame is found inside the client frame by
   searching near (10,10), where a fresh profile puts the world. The tool then
   masks, and counts separately:
   - the player's own body at the centre;
   - the paperdoll gump (logging in double-clicks the player and the server
     opens it);
   - the chat entry line.

   Whatever still differs is grouped into boxes in `report.json`, and
   `side_by_side.png` shows client | editor | diff.

**Season.** A shard sends each map a season, and the client swaps seasonal
art for it (in spring, grass tufts become flowers). The editor has no shard,
so the tool reads the season from the client checkout's
`tools\modernuo\src\Distribution\Data\map-definitions.json` (Felucca is
spring on the dev shard). `--season` overrides it.

**Not `[globallight`.** On a shared shard it changes the light for every
player, including devices under test. The comparison takes the shard's light
as it is.

## What cannot match

The client draws what the shard sends and the editor does not: mobiles,
server-placed items and decoration (lamp posts, signs), broadcast text. The
residual is reported box by box so each one can be looked at, never hidden.

## Measured 2026-09-27

Client from the main checkout, as guosweep:

| Cell | Identical, outside masks | Residual |
|---|---|---|
| 1164,1668 (wilderness) | 99.98% | 37 px: single pixels and short diagonal runs along land-tile edges on open grass |
| 1496,1628 (Britain) | 99.29% | 1,319 px: a street lamp post (945) and a hanging sign (348), both shard decoration, plus 26 px |
| 1164,1668, editor project vs client on its export | 99.84% | 303 px, mostly a wandering creature (244). The exported tree stands where the editor draws it |

Output goes to `build\world_parity\<x>_<y>[_override][_project]\`. It contains
renders of client art, so it is never committed.
