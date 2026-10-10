# tools/uopack -- bulk unpack and pack of UO art, gumps and animations

The UOFiddler-style in/out loop for Epic H (H2). `unpack` writes PNGs plus a
JSON sidecar per asset; you edit the PNGs or add new ones; `pack` reads the
folder back into asset records and hands them to `tools/uodata_write`, which
writes them into a **staged** data set. The install is never written.

```
python tools\uopack\run.py unpack --what art|land|gumps|anim|tiledata --ids 0x1B74,50581,400-410 --out <folder> [--from <data dir>]
python tools\uopack\run.py pack <folder> [--source <data dir>] [--records <folder>] [--stage <staged set>]
python tools\uopack\run.py roundtrip <folder> [--from <data dir>] [--no-reuse]
python tools\uopack\run.py from-dreadcrest <candidate folder> --out <folder> [--item 0xFFF0 --body 849 --gump-male 50849 --gump-female 60849]
python tools\uopack\run.py from-job <transfer artifact> --out <folder> --body 849 [--item 0xFFF1] [--allow-preview]
python tools\uopack\run.py selftest
```

`--from` and `--source` default to `UO_CLIENT_DATA`. `--ids` takes ids and
ranges; for `anim` they are body ids (every action and direction is unpacked).

## The folder

| Path | Sidecar fields |
|---|---|
| `art/static_0x1b74.png` + `.json` | kind `static`, id, width, height, header, `tiledata` (as read); `tiledata_write` (fields to set on pack) |
| `land/land_0x0003.png` + `.json` | kind `land`, id, tail (UOP padding, hex), `tiledata` |
| `gumps/gump_50581.png` + `.json` | kind `gump`, id, width, height |
| `anim/body_0581/a00_d0.json` + `a00_d0_f00.png`… | kind `anim`, body, action, direction, `palette` (256 x 15-bit), `transparent_index`, `frames` [png, center_x, center_y, width, height] |
| `tiledata/item_0x1b74.json` | kind `tiledata-item`, id, `fields` |

Every unpacked sidecar also has `raw_sha256` (the entry as the client stores
it) and `pixels_sha256` (what it looks like).

## From SpriteMotion: `from-job`

`from-job` reads a SpriteMotion **transfer artifact** (one folder:
`transfer.json` plus the cropped frame PNGs it lists; kind
`spritemotion.transfer-artifact`, `schema_version` 1, SpriteMotion's
`docs/transfer-artifact.md`) and writes a uopack folder like
`from-outfit-lab`'s, ready for `pack`. GUO checks the artifact by that
documented contract in `jobimport.py`; no SpriteMotion code is used or copied.

- **Read only.** `--out` must be a new or empty folder outside the artifact.
  Every check runs before anything is written.
- **Refused, in plain words:** another schema or schema version; a file whose
  sha256 does not match, or a path that is absolute, has `..` or leaves the
  folder; a PNG whose size is not its crop; a stated centre that does not
  match its crop; canvas other than 256 x 256 or anchor other than (128, 192);
  an action missing a stored direction 0-4 or a frame; an action the body's
  `anim.idx` slot cannot hold; a `coverage: preview` export unless
  `--allow-preview` (for tests).
- **What it writes.** `anim/body_NNNN/aAA_dD.json` per action and stored
  direction, frames as **RGBA** PNGs with `center_x = 128 - left`,
  `center_y = 192 - bottom` (either can be negative); an empty frame is
  `0 x 0`. With `--item`: the item art as `art/static_0xNNNN` with
  `equipment.tiledata` (and `anim` = `--body`) as `tiledata_write`, or a
  `tiledata/item_0xNNNN.json` when there is no item art. `uopack.json` keeps the
  artifact's identity, coverage, pixel policy, acceptance and provenance (with
  its redistribution classes) and the manifest's sha256.
- **Colour.** An artifact is unquantized (a real export had groups of up to
  1,456 colours). `from-job` does not quantize: `pack` builds each group's
  palette and quantizes one with more than 256 colours to one shared 15-bit
  palette (median cut), the same path as any new RGBA art. Alpha below 128 is
  transparent there; `alpha: premultiplied` colour is un-premultiplied first.
- **Fixture.** `fixtures/spritemotion_transfer_v1/` is SpriteMotion's
  synthetic CC0 fixture, copied byte for byte (origin commit in
  `test_uopack.py`). An export of a real job can hold client-derived art: it is
  as private as the job, so never commit one.

## Colour and transparency

As the client: art and gumps are 15-bit colour, **0 is transparent and opaque
black is the near-black 0x0421** (`tools/guo/uoart.py`). An animation frame is
palette indices, and a pixel that no run covers is transparent. Animation PNGs
are therefore **indexed**, with the group's palette plus one otherwise unused
index marked transparent, so their bytes survive exactly. New or repainted
frames can be RGBA: pack maps them onto the sidecar's palette when every colour
is in it, otherwise builds one (median cut past 256 colours). Alpha below 128
is "not covered".

## Byte identity

The client's own encoders leave bytes that a PNG cannot carry. Static art rows
are padded to 32 bits with leftover memory, and the last row may point back at
row 0's terminator. Some animation groups lay their frames out in their own
order. So `pack --source` re-reads each entry, and when the PNG's pixels still
hash to `pixels_sha256` and the source entry still hashes to `raw_sha256`, it
uses the original bytes. Anything edited or new is encoded fresh. **Every
record pack produces is decoded again and compared with its PNGs** before it is
written or handed on.

## Readers

`tools/guo/uoread.py` (shared with `tools/uodata_write`) reads LegacyMUL UOP
(the name hash is the client's `CreateHash`) and MUL+IDX, including the
zlib+BWT gumps modern installs ship (flag 3, a port of `BwtDecompress.cs`),
art, gumps, `anim.mul` groups and both tiledata layouts. The codecs are in
`uocodecs.py`, on top of `tools/guo/uoart.py`.

## What has been run

2026-09-27, the owner's install (UOP art and gumps, MUL animations, new tiledata).

| Check | Result |
|---|---|
| unpack 30 statics, 18 land, 23 gumps, bodies 581/400/1 (415 anim groups), 1 tiledata item; `roundtrip` | **486/486 records byte-identical** to the install (485 reused unchanged) |
| the same with `--no-reuse` (the encoders alone) | 457/486 identical; the rest are statics whose original row padding is leftover memory; all 486 decode pixel-identical |
| a sweep of random entries, encoders alone | gumps 223/223, land 125/125, animation groups 1,838/1,863 identical |
| `from-dreadcrest` + `pack` (Codex's candidate, read only) | 179 records (175 animation groups, item art + tiledata, 2 paperdoll gumps); all decode equal to their PNGs; the 175 groups match Codex's own encodings pixel for pixel (0 of 457,411 pixels differ), none needed quantising |
| `test_uopack.py` (pytest, pooled and in CI; synthetic install, no client data) | 13 tests pass, from-job included |
