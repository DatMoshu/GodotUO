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
| `test_uopack.py` (pytest, pooled and in CI; synthetic install, no client data) | 12 tests pass |
