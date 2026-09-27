# tools/editor_live

Checks the editor's live tier (ADR-0012) end to end: **two editors and a
game client**, on the private shard (`tools/editor_shard`, 127.0.0.1:2594),
never the shared dev shard.

```
python tools\editor_live\run.py [--export DIR | --no-export] [--facet 0|1] [--windowed]
```

Headless by default: no window takes the desktop's focus, and the client's
UltimaLive log line is the evidence that it received the block. With
`--windowed`, editor A and the client open windows and the client photographs
its frame (`client.png`).

1. Installs the editor bridge in the private shard and restarts it with the
   world export (`--export`, default `build\shard_proof_export`) first in its
   data directories.
2. Clears the client's UltimaLive copies for the private shard's name
   (`%ProgramData%\GUO-Editor-Private`), so the client starts from the export.
3. Logs this checkout's client in (probe account, scratch home, `files_override`
   = the export's) and stands it at 1164,1668.
4. Starts editor B (`--guo-editor-live follow`) and editor A, both headless unless `--windowed`
   (`--guo-editor-live send`); both connect to the bridge.
5. When the bridge reports the client on UltimaLive, tells A to go. A stamps
   a tree at 1167,1666 through the World tab's editor, and runs `[where` as
   the client's character.
6. Writes `build\editor_live\summary.json` with:
   - A's send time and round trip;
   - clients pushed to and editors relayed to;
   - B's receipt time and whether its world has the tree;
   - the bridge's log lines;
   - the client's UltimaLive lines;
   - `client.png`.

`--facet 1` runs the whole check on Trammel instead of Felucca: both editors
work on map1, and the client is moved there (`[self set map trammel`). The
client is always moved to the facet under test first, because the private
shard saves where the character was left. The stamp waits until the client
reports standing at the cell on that facet.

With `--no-export` the shard and the client use the install alone, with no
`files_override`. The client must build its UltimaLive map copies itself, which
on a UOP-only install means converting each `map<N>LegacyMUL.uop` (ADR-0012).
Every copy it made is then checked block for block against the install
(`ultimalive_map_copy` in the summary).

It passes only if the block reached both the client and editor B, B got it
in under 1 s, the command ran, and the client logged no UltimaLive exception.

Measured 2026-09-27: A to B in 35-46 ms, round trip to the shard's
acknowledgement 51-57 ms. The client wrote the statics in the same second,
and `client.png` shows the tree.
