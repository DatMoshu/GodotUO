---
name: uo-data
description: "Author new UO content (item art, gumps, animations, tiledata) into a staged data set and prove it in game: check, scan free slots, reserve a pack's ranges, unpack or build the PNG folder, pack it into the stage, verify the read-back and the untouched install, then wear or place it on the private shard and film it. Never writes the install. Use for 'put this new item in the game', for a new wearable, and before claiming authored content works."
argument-hint: "<pack> [folder <png folder> | dreadcrest <candidate folder>] [--item 0xNNNN] [--ranges <policy.json>] [--no-play]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# UO Data

Runs the Epic H flow end to end (ADR-0022) and reports what actually
happened. The tools:

- `tools\uodata_write\run.py`: the stage, the free-slot scans, the ranges, the
  writers and verification. Its tests are `test_uodata.py`.
- `tools\uopack\run.py`: PNG and JSON sidecars in and out; read its README.
- `tools\uodata_write\play.py`: the private shard, a client, the item worn,
  and a clip.

This skill is the order to run them in, and the honesty of the report.

Arguments:

- `<pack>` names the content set. `moshu` is the owner's; its item range is
  fixed at 0xFFF0-0xFFFE in `tools\uodata_write\ranges.json`.
- `folder <dir>` packs a PNG folder laid out as `tools\uopack` describes.
- `dreadcrest <dir>` builds the folder from a Codex wearable candidate first.
- `--ranges <file>` is a shard maintainer's range policy. It can also come
  from `UO_DATA_RANGES`.
- `--no-play` stops after step 5.

The stage is `build\uodata\<pack>`, one per pack. Write only there. **Never
write under `UO_CLIENT_DATA`**: the tools refuse to, and the skill must not
try another route.

---

## 0. Before anything heavy

```
powershell "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB"
```

- Under 16 (GB): stop and report. Run one heavy job at a time.
- `python tools\uodata_write\test_uodata.py` must print `test_uodata: OK`.
  It uses synthetic files only. A failure stops the flow: report the `FAIL`
  lines verbatim.

## 1. Scan

```
python tools\uodata_write\run.py scan --stage build\uodata\<pack>
```

This reports the free item ids, the free people bodies (and how many also have
both paperdoll gumps free), and the free gumps. Put the numbers in the report.
A new wearable needs one item id and one body with both gumps free.

## 2. Reserve the pack's ranges

```
python tools\uodata_write\run.py reserve --stage build\uodata\<pack> --pack <pack> [--statics 16] [--bodies 4] [--ranges <file>]
```

`slots.json` in the stage records the ranges.

- A pack already reserved keeps its ranges; running it again changes nothing.
- A policy range that is not free is refused, and the conflicting ids are
  listed. Report them. Never edit `ranges.json` to get past it; that is the
  maintainer's decision (ADR-0022, "Ranges").

## 3. The PNG folder

Pick the source from the arguments:

| Source | Command |
|---|---|
| `dreadcrest <dir>` | `python tools\uopack\run.py from-dreadcrest <dir> --out build\uodata\<pack>_src --item <id> --body <body> --gump-male <50000+body> --gump-female <60000+body>` with the ids from `slots.json` |
| `folder <dir>` | use it as it is; read its sidecars' `id` fields and check that every one is inside the pack's ranges |
| editing existing art | `python tools\uopack\run.py unpack --what art\|gumps\|anim\|tiledata --ids ... --out <dir>`, edit the PNGs, keep the ids |

An id outside the pack's ranges is a stop: say which one, and do not pack.

## 4. Pack into the stage

```
python tools\uopack\run.py pack <folder> --stage build\uodata\<pack>
```

- Every record is decoded again and compared with its PNGs before it is
  written.
- Exit 0 means verified. Lines start with `[uopack]`.

For the Dreadcrest alone,
`python tools\uodata_write\run.py dreadcrest --stage build\uodata\<pack> --source <dir> --pack <pack>`
does steps 2-4 in one go.

## 5. Verify

```
python tools\uodata_write\run.py verify --stage build\uodata\<pack>
```

It must print `install unchanged: True`. Report the read-back count from
step 4 (`N/N records equal`) and the files the stage holds (`stage.json`).
**Anything other than N/N and True is a failure. Say so and stop.**

## 6. In game (unless `--no-play`)

This needs the private shard (`tools\editor_shard`, 127.0.0.1:2594) set up,
and a windowed client that never takes the focus. **Never the shared shard on
2593.**

```
python tools\uodata_write\play.py --stage build\uodata\<pack> --item <id> --clip build\uodata_play\<pack>.mp4
```

It starts the shard with the stage first in its data directories and logs a
client in with the stage's `files_override`. The bridge's `equip` op puts the
item on the character. The client's dump, a frame and a walking clip follow,
then everything stops.

Each run writes to its own folder, `build\uodata_play\desktop-<time>\` (or
`device-<time>\`), printed as `[play] output:`. Its `report.json` has:

- `equip.ok`, `can_equip`, `check_equip` from the shard;
- `worn_seen_by_client`;
- `walked`;
- `clip`.

Worn on the server and seen by the client are two different claims. Report
both. Look at `watch\equipped.png` in that folder yourself before calling
the item visible.

**On a device (the Thor):** the device reads the stage through
`--files-override` and reaches the loopback-only shard through `adb reverse`:

```
python tools\android\run.py push-stage build\uodata\<pack> --reverse 2594
python tools\android\run.py export --args "--files-override <printed path> --host 127.0.0.1 --port 2594"
python tools\android\run.py install
python tools\uodata_write\play.py --stage build\uodata\<pack> --item <id> --device 900
```

Then start the app. `play.py --device` serves the shard, equips the item as
soon as the probe character is online, and holds the shard for that many
seconds. `push-stage` never changes the device's copy of the install: the
staged files go beside it as `stage-<pack>-<name>`. Photograph the device with
the android tool, not the desktop.

An item with no layer in its tiledata cannot be worn: `equip` refuses it.
`play.py` does not place items yet. Report it as staged and verified, not seen
in game.

## 7. Report

One table:

| Step | Command | Result |
|---|---|---|
| tests | test_uodata.py | OK / the FAIL lines |
| scan | ... | free ids / bodies / gumps |
| ranges | ... | the pack's ranges |
| pack | ... | N/N decoded equal |
| verify | ... | install unchanged: True/False |
| in game | play.py | equip ok, worn seen, walked, clip path |

Give every path as a `file:///` link. Then say plainly what is proven and what
is not. For example: "worn and walked on ModernUO; not tried on a MUL-only
install; not tried on the Thor".

## Never

- Write, copy into, or `files_override` anything inside `UO_CLIENT_DATA`.
- Commit anything under `build\`, a stage, or client data. Staged files are
  copies of proprietary data.
- Hand out 0xFFFF, or an id outside the pack's ranges.
- Publish a clip or frame anywhere unless the owner asked.
