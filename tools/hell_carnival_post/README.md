# hell_carnival_post

Builds the process post and the shareable artwork bundle for Codex's
Hellmaw / Cosmic Carnival dungeon art. It reads
`build/uo_original_expansion/hell_carnival/` in the main checkout (read only)
and writes to `build/hell_carnival_post/`.

The source folder resolves as: `--src <dir>`, then env `GUO_HELL_CARNIVAL_SRC`, then
`build/uo_original_expansion/hell_carnival/` in this checkout, then in the main checkout
(found through `git rev-parse --git-common-dir`). Rar comes from env `RAR_EXE`, then `PATH`,
then WinRAR's default install folder. See `paths.py`.

```
python tools/hell_carnival_post/images.py    # attachments/: wide A|B|B+props per dungeon, 2x A/B crops, seam sheet
python tools/hell_carnival_post/bundle.py    # stage/ + upload/<name>.rar (Rar -m5 -ma5 -s -md1g -rr3%, -v490m if large)
```

The bundle takes art only from an allow-list, and it refuses to pack if any
client-derived file slips in (MUL/UOP/IDX, references/, fixtures/, worlds/,
local-only-clients/, caches, configs). Its README lists what was left out and
why. `upload/` is the folder to drag into the owner's AI Drive folder: the
.rar, the post text and the post images.
