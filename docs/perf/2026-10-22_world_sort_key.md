# World sort: keys instead of a comparison, a stamp instead of a set

Date: 2026-10-22. Branch `work/perf-sort-20261022`. Offline work; no live
measurement yet (the seat's account and exclusive window are held by the
director). The live numbers to beat are the Codex perf seat's main
validation of the same day: britain-bank sort 2.2-2.5 ms, cover 1.1-5.6 ms
per frame at 2560x1440.

## What changed

1. **`RenderLists.DrawWorld` sorts 64-bit keys, not drawables.** The sorted
   world pass used `List<Drawable>.Sort(Comparison)`: one delegate call per
   comparison over a 24-byte struct. `DepthKey.Of(depth, seq)` maps the
   float depth to an unsigned integer with the same order (sign bit flipped
   for positives, all bits for negatives, signed zero folded) and puts the
   queue index in the low half. `Array.Sort(ulong[])` is a primitive
   introsort; the low half of each sorted key says which drawable to draw.
   The keys array lives on the RenderLists and grows only when a frame
   needs more.
2. **`Land.CoverStamp` replaces the `HashSet<Land>` of covering tiles.**
   The set was asked once per candidate tile in `CoverFromBelow` (a dungeon
   frame asks about tens of thousands) and once per land drawable in
   `DrawWorld`. A tile now carries the stamp of the frame that queued it;
   the stamp advances in `Clear`.
3. **`CoverFromBelow` looks a chunk up when it changes, not per tile.** The
   cover square is walked a row at a time, so eight neighbours share a
   chunk. `LandAt` takes a caller-held chunk and position and goes through
   `Map.GetChunk2` only when the tile leaves it. An unloaded chunk is
   remembered as null for the same reason.

Nothing about which tiles cover, or in what order anything draws, changes:
the proof is `tools/render_sort_tests`.

## Proof and isolated timing

`dotnet run --project tools/render_sort_tests -- --bench` on the author's
machine (Release, in-process, advisory):

| draws | List.Sort(comparison) | key sort | ratio |
|---|---|---|---|
| 2,000 | 0.463 ms | 0.016 ms | 28.6x |
| 10,000 | 1.198 ms | 0.313 ms | 3.8x |
| 30,000 | 1.690 ms | 1.110 ms | 1.5x |

225,561 order checks over five input shapes (both signs, piles of ties,
signed zeros, nearly sorted, extreme magnitudes) at sizes to 40,000: the
key order equals the comparison order everywhere. `launchers/dev/smoke.bat`
runs the proof.

## What to measure live

The perf gate (`launchers/dev/perf_gate`, PR 126 contract) before and
after, same fixture, and the probe's `sort` and `cover_land` stage times on
britain-bank and dungeon. Counts (draw calls, batcher items) must not move:
this change touches order of work, not the work.
