# Author UO Data

Put new content in the game, such as a new item with its own art, paperdoll
picture and animation, without touching your UO install. The decision is
ADR-0022; the file layouts are in `docs\data_formats.md` §14. In Claude Code,
`/uo-data <pack>` runs everything below.

## The idea

- **A stage, not your install.** The tools copy only the files they change
  into `build\uodata\<pack>\`, write there, and check afterwards that every
  original still hashes the same. A stage inside the install is refused.
- **Append-only.** New art and gumps get a new block chained onto the UOP
  file, animations are appended to `anim.mul`, and only the new item's
  tiledata record is written in place. Nothing that was there moves.
- **Reserved ranges.** Each pack gets its own ids, recorded in `slots.json`,
  so two packs never collide. The shipped default pack has item ids
  0xFFF0-0xFFFE. 0xFFFF is never used. A new pack name gets its own free
  range when you `reserve` it.
- **Read back.** Every record is read back and compared before the run
  says it worked.

## Steps

```
python tools\uodata_write\test_uodata.py
python tools\uodata_write\run.py scan    --stage build\uodata\mypack
python tools\uodata_write\run.py reserve --stage build\uodata\mypack --pack mypack
python tools\uopack\run.py unpack --what art --ids 0x1B74 --out build\uodata\mypack_src
    (edit or add PNGs; keep ids inside the pack's ranges)
python tools\uopack\run.py pack build\uodata\mypack_src --stage build\uodata\mypack
python tools\uodata_write\run.py verify  --stage build\uodata\mypack
python tools\uodata_write\play.py --stage build\uodata\mypack --item <your item id> --clip build\uodata_play\mypack.mp4
```

- The first command runs the writer tests. They use tiny synthetic files, so
  no client data is needed.
- `play.py` needs the private dev shard ([Dev Shard](Dev-Shard.md)). It starts
  the shard with the stage first in its data folders, logs a client in that
  reads the stage (`files_override`), puts the item on the character, and
  films a short walk.

## Playing with a stage yourself

Point the client at the stage in your `settings.json`:

```json
{ "files_override": "<your stage folder>/files_override.txt" }
```

The shard needs the same files: list the stage folder first in its data
directories. Both have to read the same tiledata, or the server will not agree
with the client about what the item is.

## From the editor

The editor's Assets > Bulk tab runs the same unpack and pack from the UI
(see [Editor](Editor.md)). To share art with other players, make a pack
instead of sending staged files: see [Content Packs](Content-Packs.md).

## On a phone or handheld

`python tools\android\run.py push-stage build\uodata\mypack --reverse 2594`
copies the staged files next to the device's client data, under their own
names, and writes an override file there. It prints the export command that
bakes `--files-override` into the build. `--reverse` lets the device reach
the private shard on this PC. Then run
`python tools\uodata_write\play.py --stage build\uodata\mypack --device 900`
to serve the shard and equip the item when the device's character logs in.

## For shard maintainers: your own ranges

If your shard already uses ids that are not in players' installs, bar them,
and move packs if you need to. Write a file shaped like
`tools\uodata_write\ranges.json`:

```json
{ "never": { "static": [[61440, 65535]] },
  "packs": { "mypack": { "static": [59392, 59406] } } }
```

Then pass it with `--ranges <file>`, or set `UO_DATA_RANGES`.

- Your `never` ranges are added to the default's.
- Your pack ranges replace the default's.
- A range that is not free is refused, with the ids that are in the way.

## What has been shown

The Dreadcrest shield (item 0xFFF0, animation body 849) was:

- staged, 179/179 records read back, with the install unchanged;
- worn on the private ModernUO shard, where the client showed it on the
  paperdoll and while walking.

Not yet tried: a MUL-only install or another shard emulator. The phone steps
above are written, not verified.
