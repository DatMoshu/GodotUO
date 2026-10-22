# tools/visual_ab

Visual A/B (B7): ClassicUO and GUO, with and without the land array, in the
same spots. Each client runs once per scene with `GUO_SHOT_DUMP` set
(`src/Bootstrap/ShotDump.cs`, compiled into both), does the scene's steps and
saves its own screenshots; GUO runs plain, `--merged-land=array` and with
`--merged-cover`. Output per scene in `build/visual_ab/<scene>/`: each shot,
a labelled sheet, and a diff of each GUO variant against plain GUO. The diff
table counts every differing pixel and, apart, the isolated ones (no differing
neighbour): those are single texels of stretched riverbank tiles that the land
array's nearest sampling picks from the other side of a texel edge (PERF-V7,
[the cover regression note](../../docs/perf/2026-10-19_cover_regression.md)),
and are the only difference a variant may have. Needs a shard.

## Run

```
python tools/visual_ab/run.py [--port 2598] [--size 1600,900] [--build] [--scene NAME ...]
python tools/visual_ab/run.py --sheets        redraw the sheets from shots already taken
```

## Tests

```
python -m pytest -q tools/visual_ab
```

The diff split (all pixels, isolated pixels) is tested on synthetic pictures.
The run itself is the check for the clients, and its exit code says whether it passed.
