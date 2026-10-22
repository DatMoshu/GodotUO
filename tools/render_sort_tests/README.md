# render_sort_tests

Proves the three engine-free pieces the sorted world pass (`RenderLists`)
rests on: the key sort (`DepthKey`) orders draws exactly as the comparison
sort it replaced (depth, then queue order), also over a queue shaped like
the real one (shadow/body pairs, meshed statics, covering land, Seq == index);
the per-frame cover mark (`CoverStamp`) is void after a frame, idempotent
within one, never shared between two owners or with a fresh tile, and
answers exactly as the set per owner it replaced over random frames under
the supported lifecycle (one owner's frame at a time; an interleaving is
stated as unsupported and asserted in debug builds); the
chunk cursor (`ChunkCursor`) looks a chunk up once per chunk a walk passes
through, never for a negative tile, and remembers an unloaded chunk.

```
dotnet run --project tools/render_sort_tests            # proof; exit 1 on a mismatch
dotnet run --project tools/render_sort_tests -- --bench # proof, then timings
```

The sort proof runs five input shapes (spread depths of both signs, piles
of ties, signed zeros, a nearly sorted frame, extreme magnitudes) at sizes
up to 40,000 draws. `launchers/dev/smoke.bat` runs the proof. Timings are
advisory and need no client data, shard or Godot. What the proof cannot
reach is the engine side: that `Map` hands the cursor the loaded chunk and
that the client draws the same pixels; those are the live gate's.
