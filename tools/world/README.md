# tools/world

Turns a **world project** (the editor's map edits: whole replaced 8x8 blocks
as JSON, `docs/data_formats.md` §9, ADR-0011) into map files a server and a
client can read, and checks them. The client install is only ever read.

```
python tools\world\run.py blocks  [--project DIR]
python tools\world\run.py export  [--project DIR] [--out DIR] [--force]
python tools\world\run.py verify  [--project DIR] [--out DIR]
launchers\pipeline\04_world_export.bat [same flags]     export, then verify
```

`--project` defaults to `UO_WORLD_PROJECT`; `--out` to `<project>\export`.

## export

For every facet the project touches, copies the install's land file
(`map<N>LegacyMUL.uop`, or `map<N>.mul`), `staidx<N>.mul` and
`statics<N>.mul` into the export folder and patches the copies:

- each replaced block's 64 land cells are written in place, at the offset
  `MapLoader` computes (UOP: entry `n >> 12`, then `(n & 4095) * 196`),
  keeping the block header;
- its statics are appended to the statics copy and its `staidx` entry is
  pointed at them (an empty block gets offset -1, length 0).

Every block the project does not replace stays byte for byte the install's.

Beside the files it writes:

| File | For |
|---|---|
| `files_override.txt` | the client: upstream's `UOFilesOverrideMap` format, lower-cased file name `=` path. Give it to ClassicUO's `files_override` setting (or `-filesoverride`) |
| `export.json` | the record: project, install fingerprint, facets, blocks, and each file's SHA-1 |

A server reads the export by listing the folder **first** in its data
directories (ModernUO: `dataDirectories` in `modernuo.json`), ahead of the
install; it then finds the patched files there and everything else in the
install.

**Refusals.** An `--out` inside `UO_CLIENT_DATA` is refused before anything
is written. A project whose `base.fingerprint` differs from this install's is
refused unless `--force`; its block offsets may not mean the same blocks.

## verify

Reads the export back with `tools/guo/uomap.py`, a reader independent of the
port's C#, and checks that every replaced block equals the project's (land
ids, altitudes, statics as a multiset) and that every other block (land bytes
and statics bytes) equals the install's. On map0 that is 458,752 blocks, in
about a second.

## blocks

Lists what a project replaces, with each block's cell range.

## pack: sending your edits to a shard owner

```
python tools\world\run.py pack [--project DIR] [--out FILE.zip]
```

Zips the project's **edits and nothing else**: `project.json`, the changed
blocks (`blocks\`) and the replaced art, gumps and hues (`assets\`, ADR-0020),
plus a `README.txt` telling the receiver how to apply it. The default output
is `build\world_pack\<project>.zip`.

A pack holds no client data, so it can be sent to anyone. An **export**
folder can't: it holds copies of the sender's map files and `hues.mul`, which
are the install's. So what travels is the pack, and each receiver (the shard
owner, or each player) runs `export` against their own install:

```
python tools\world\run.py export --project <unzipped folder> --out <export folder>
python tools\world\run.py verify --project <unzipped folder> --out <export folder>
```

The shard lists `<export folder>` first in its data directories. Players
point `files_override` at its `files_override.txt`. If the receiver's install
differs from the sender's (map file sizes, `project.json`'s fingerprint),
`export` refuses until they check the client version and pass `--force`.

`pack` checks every file before writing anything:

- block files must parse;
- land art must be 44x44, statics at most 1024x1024 and gumps at most
  2048x2048;
- hues must have 32 colours.

A pack that leaves your machine therefore exports. An `--out` inside
`UO_CLIENT_DATA` is refused.

Checked 2026-09-27: a pack of one block and four assets (4 KB) was unzipped
into another folder, then exported and verified from there. A 40x40 land tile
was refused, and so was an `--out` in the install.

## World objects (ADR-0014)

A project's `shard\objects.json` (spawners and placed items,
`docs\data_formats.md` §13) exports through a backend adapter
(`tools\world\backends\`, `UO_SHARD_BACKEND`, default `modernuo`) as the
server's own files, under `<export>\shard\`:

- the ModernUO spawner JSON and decoration cfg;
- `guo_objects.json`, the manifest;
- `APPLY.txt`.

`verify` reads the files back the way ModernUO parses them and compares them
with the model. `pack` includes `objects.json`.

The files alone change nothing on a running shard, because ModernUO keeps
placed objects in its save. They take effect through a **sync**:

- on the private instance, `tools\editor_shard start --objects <export>`;
- on any other ModernUO shard, the GM commands in `APPLY.txt`, which add and
  update but cannot delete.

## apply-commands: world objects on a shard without GUO's bridge

```
python tools\world\run.py apply-commands [--project DIR] --host H --port P [--dry-run]
```

This is the fallback of ADR-0014. A GUO client, logged in as a GM (the
configured account), types the server's own commands (ModernUO) to bring the
shard from what this fallback placed there before to what the project holds.

| Change | Commands |
|---|---|
| Place an item | `[go x y z`, then `[TileXYZ x y 1 1 z Static <id> set Name <tag> [Hue <h>]` |
| Place a spawner | `[TileXYZ ... Spawner <entry> set Name <tag> Count ... HomeRange ...`, then `[Range 0 Respawn where Spawner Name == <tag>` |
| Remove | `[go` to the recorded cell, then `[Range 0 Remove where <Type> Name == <tag>` |
| Move or change | remove, then place |

**It cannot remove what it did not place.** Every placement carries a name
tag of its own, `guo-<id8>-<nonce>`, so no two placements share one. It removes only by that tag, only on the
cell it recorded, and only ids in its record: `<project>\shard\applied\<host>_<port>.json`,
written after the commands went through. Shard content carries no GUO tag,
so no command it types can match it, even an item with the same art on the
same cell.

Limits:

- A spawner with more than one entry cannot be made with `[TileXYZ`; it is
  skipped and named.
- Items and spawners show their tag as their name when clicked.
- A run is slow: the client waits for each command's answer to fall quiet,
  about a minute per command.
