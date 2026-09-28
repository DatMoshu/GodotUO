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

## Re-run on the land-only atlas (596a3e7)

GUO3's fix (work/integration 596a3e7) gives land its own Art atlas under
the flag and creates the array with spare layers, so a new page is one
layer copy, not a rebuild. The same Thor, the same route and flags, four
alternating pairs (13:49-14:02), each a fresh install and start. The probe
now also reports the array's capacity and the video memory, read at the
end of the walk.

| Thor, walk | array 1 | off 1 | array 2 | off 2 | array 3 | off 3 | array 4 | off 4 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| mean ms | 16.91 | 16.89 | 16.88 | 16.96 | 16.89 | 16.79 | 16.84 | 17.30 |
| p95 ms | 21.86 | 21.58 | 21.64 | 22.43 | 22.44 | 18.39 | 18.12 | 27.18 |
| p99 ms | 30.09 | 31.16 | 29.15 | 31.69 | 29.70 | 23.18 | 27.37 | 35.66 |
| max ms | 35.71 | 35.17 | 34.42 | 39.06 | 34.75 | 35.98 | 29.22 | 50.24 |
| land-array uploads (all bumps, no rebuilds) | 3 | 0 | 0 | 0 | 0 | 0 | 4 | 0 |
| upload frames and the next: mean ms | 22.68 | – | – | – | – | – | 22.76 | – |
| upload frames and the next: max ms | 30.09 | – | – | – | – | – | 29.21 | – |
| array layers used / created | 2 / 4 | – | 2 / 4 | – | 2 / 4 | – | 2 / 4 | – |
| video memory, MiB | 348.0 | 267.4 | 348.0 | 267.3 | 348.3 | 267.1 | 347.1 | 266.8 |

- **Uploads are rare now:** 3, 0, 0 and 4 in a 12 s walk, against 16 and 6
  before. None is a rebuild.
- **An upload still costs a vsync.** The upload frames and the frames after
  them average 22.7 ms, about 6 ms over the mean, which is what one upload
  cost before. So in two walks of four, the array adds three or four missed
  vsyncs. The fix took away the count, not the cost of each.
- **Those hitches are inside the walk's own noise.** Their worst, 30 ms,
  is below the p99 of three of the four walks without the array (23-36 ms,
  worst frame 35-50 ms). Across the four pairs: mean 16.88 against 16.99 ms,
  p99 29.1 against 30.4 ms. Draw calls vary between pairs (193 to 448), so
  they are not compared here.
- **GPU memory: +80 MiB.** The array is 4 layers of 16 MiB (64 MiB), of
  which 2 are spares (32 MiB). The rest (about 16 MiB) is the land-only
  Art page. Texture memory is 188.8 against 108.5 MiB.

Measured, for the owner's rule ("the upload frames no longer miss vsync"):
**not met as worded.** When an upload happens, the frame still misses
vsync, but uploads happen in half the walks, a few times each, and never
worse than the walk's own hitches without the array. Raw files are in the
measuring worktree's build/android/m5c (gitignored).

## Decision (re-run)

The owner's call, relayed by the director on 2026-09-28: **B7 is on for
Android.** `--merged-land=array` is the Android default, and
`--merged-land=off` turns it off. The desktop, the Deck and the web stay off.
The record is ADR-0017's amendment of the same date.

Checked on the Thor with the default build (no `--merged-land` flag),
`--perf-parity` against the per-chunk land on the same frames (14:03-14:05):

| Scene | pixels that differ | draw calls, array | draw calls, off | mean ms, array | mean ms, off |
|---|---:|---:|---:|---:|---:|
| login | 0 of 2,073,600 | 44 | – | 8.48 | – |
| open field | 0 of 2,072,193 | 165 | 205 | 16.79 | 16.79 |
| walk (4 uploads) | – | 195 | 231 | 16.79 | 16.80 |
