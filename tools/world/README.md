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
