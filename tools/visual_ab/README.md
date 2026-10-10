# tools/visual_ab

Visual A/B (B7): ClassicUO and GUO, with and without the land array, in the
same spots. Each client runs once per scene with `GUO_SHOT_DUMP` set
(`src/Bootstrap/ShotDump.cs`, compiled into both), does the scene's steps and
saves its own screenshots; GUO runs plain, `--merged-land=array` and with
`--merged-cover`. Output per scene in `build/visual_ab/<scene>/`: each shot,
a labelled sheet, and a diff of each GUO variant against plain GUO, which must
be empty. Needs a shard.

## Run

```
python tools/visual_ab/run.py [--port 2598] [--size 1600,900] [--build] [--scene NAME ...]
python tools/visual_ab/run.py --sheets        redraw the sheets from shots already taken
```

## Tests

No automated tests: the run itself is the check, and its exit code says whether it passed.
