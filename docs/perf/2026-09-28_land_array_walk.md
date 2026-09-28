# The land array on a walk (M5, B7): what the uploads cost

2026-09-28. Review finding R1-1: `--merged-land=array` re-uploads a whole
2048² land layer (16 MiB) when its Art atlas page changes, and every scene
measured so far stood still. The `walk` perf scene (GUO3, the clock-timed
version in work/integration a007e77) runs the player from the open field
south of Britain through ground that loads as it comes into view, turning
where blocked. It reports `LandPages.Uploads`, and separately the frames
that carry an upload: the upload frame and the next one, since an upload
lands in the frame after the one that drew it.

## AYN Thor

The debug APK from work/integration a007e77, one export with
`--merged-land=array` and one without (Android's current default),
`--perf-probe --perf-scene walk`, silent. Each pass is a fresh app start.
46 tiles moved and 1 draw per frame in every pass. The Thor's frame rate
sits at its 60 Hz panel (a mean of about 16.9 ms), so a frame over 16.7 ms
misses a vsync.

The first pair ran while another session installed and ran a build on the
same Thor (11:27-11:29). Each pass finished in one app process, so neither
was replaced mid-run, but the timings may carry that load. The second pair
ran alone.

| Thor, walk | array, run 1 | off, run 1 | array, run 2 | off, run 2 |
|---|---:|---:|---:|---:|
| frames | 704 | 713 | 711 | 710 |
| mean ms | 17.07 | 16.85 | 16.89 | 16.91 |
| p95 ms | 22.36 | 19.55 | 21.32 | 20.07 |
| p99 ms | 28.94 | 27.44 | 28.80 | 30.78 |
| max ms | 35.18 | 37.41 | 37.14 | 44.41 |
| land-array uploads | 16 (16 frames, 1 at most) | 0 | 6 (6 frames) | 0 |
| MiB uploaded per frame | 0.36 | 0 | 0.14 | 0 |
| upload frames and the next: mean ms | 19.97 | – | 24.98 | – |
| upload frames and the next: max ms | 30.21 | – | 37.14 | – |

## Desktop (GUO3)

GUO3's runs, 2026-09-28 11:19-11:20 (labels m5c_walk_array and
m5c_walk_off): RTX 4090, Windows, 1280x720, zoom 0.7, vsync and the frame
cap off, an optimised build, 12 s of walking after 180 frames of settling.

| Desktop, walk | array | off |
|---|---:|---:|
| tiles moved | 47 | 46 |
| mean ms | 0.560 | 0.573 |
| p95 ms | 0.794 | 0.773 |
| p99 ms | 1.15 | 1.07 |
| max ms of any frame | 22.70 | 16.12 |
| draw calls | 49.1 | 133.6 |
| land-array uploads | 30 (29 frames, 2 at most) | 0 |
| MiB uploaded per frame | 0.02 | 0 |
| upload frames and the next: mean ms | 2.91 (+2.35 over the mean) | – |
| upload frames and the next: max ms | 7.81 | – |

That is 30 uploads in 12 s, about 2.5 a second. Uncapped, an upload frame
costs +2.35 ms against a 0.56 ms frame. The array cuts draw calls by 63%.

## Reading it

- **The mean doesn't move.** The array and plain passes are within 0.2 ms
  of each other on the Thor. Upload frames are too few to shift a mean.
- **The upload frames do.** On the Thor each one costs 3-8 ms over the mean.
  At 60 Hz that is a missed vsync, and at +8 ms sometimes two. p95 goes up
  1.2-2.8 ms. The desktop's RTX 4090 pays +2.35 ms per upload frame, with
  nothing waiting on vsync.
- **The count varies from run to run** (16 and then 6 on the same route),
  probably with how much of the Art atlas is already filled when the walk
  starts. The cost per upload is the thing to fix, not the count.
- **The worst frame is no worse** than without the array (35-37 ms against
  37-44 ms): the uploads add frequent small hitches, not a large one.

## Decision

The director's call (2026-09-28): **B7 is not clean**, so
`--merged-land=array` stays off on Android. The owner's rule for turning it
on needs a clean walk here and a clean visual A/B. The fix direction, for
GUO3: upload only the rows the atlas changed, or give land its own atlas
pages, so a static landing on a shared Art page doesn't re-upload a land
layer.

Raw files: `perf_walk-{array,off}.{md,json}` from both pairs, kept in the
measuring worktree's build/android/m5 and m5b (gitignored).
