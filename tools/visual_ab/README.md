# tools/visual_ab

Visual A/B (B7): ClassicUO and GUO, with and without the land array, in the
same spots. Each client runs once per scene with `GUO_SHOT_DUMP` set
(`src/Bootstrap/ShotDump.cs`, compiled into both), does the scene's steps and
saves its own screenshots; GUO runs plain, `--merged-land=array` and with
`--merged-cover`. Output per scene in `build/visual_ab/<scene>/`: each shot,
a labelled sheet, and a diff of each GUO variant against plain GUO, which must
be empty. The diff table counts every pixel that differs in any channel by any
step (exact), and beside it, for information, how many of those are isolated
(no differing neighbour): the shape of a difference, not its cause or its
acceptability. A non-empty diff is read from the diff picture, whatever its
shape (PERF-V7, [the cover regression note](../../docs/perf/2026-10-19_cover_regression.md)).
Needs a shard.

## Run

```
python tools/visual_ab/run.py [--port 2598] [--size 1600,900] [--build] [--scene NAME ...]
python tools/visual_ab/run.py --sheets        redraw the sheets from shots already taken
```

## Tests

```
python -m pytest -q tools/visual_ab
```

The exact count (single-channel one-step controls in R, G and B, black against
one blue step) and the isolated split are tested on synthetic pictures.
The run itself is the check for the clients, and its exit code says whether it passed.
