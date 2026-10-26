# render_sort_tests

Proves the three engine-free pieces the sorted world pass (`RenderLists`)
rests on: the key sort (`DepthKey` and production `WorldDepthSort`) orders draws exactly as the comparison
sort it replaced (depth, then queue order), also over a queue shaped like
the real one (shadow/body pairs, meshed statics, covering land, Seq == index);
the per-frame cover mark (`CoverStamp`) is void after a frame, idempotent
within one, never shared between two owners or with a fresh tile, and
answers exactly as the set per owner it replaced over random frames under
the supported lifecycle (between a list's first cover mark and its draw no
other list marks; an interleaving is stated as unsupported); the
ownership check (`CoverOwner`) passes the client's real sequence (world
Clear, gather, draw; the UI manager's two lists cleared and drawn after,
and UI child lists drawn without a Clear, also mid-world-frame), reports
the interleaved case at the first list's draw and keeps it reported until
that list's own Clear or draw (A/B/A, and B releasing before A resumes),
and lets an abandoned list give way; the
chunk cursor (`ChunkCursor`) looks a chunk up once per chunk a walk passes
through, never for a negative tile, and remembers an unloaded chunk.

```
dotnet run --project tools/render_sort_tests            # proof; exit 1 on a mismatch
dotnet run --project tools/render_sort_tests -- --bench # proof, then timings
```

The sort proof runs five input shapes (spread depths of both signs, piles
of ties, signed zeros, a nearly sorted frame, extreme magnitudes) at sizes
through 65,537 draws, including both radix thresholds. Each production result
also matches the full unsigned-integer `Array.Sort` oracle. Separate BatcherProbe
fixtures cover every transformed byte, signed NaN payloads, inactive tails,
all constant-byte skip combinations and warmed allocation behavior.
`launchers/dev/smoke.bat` runs the engine-free proof. Timings are
advisory and need no client data, shard or Godot. What the proof cannot
reach is the engine side: that `Map` hands the cursor the loaded chunk and
that the client draws the same pixels; those are the live gate's.

## Pixel-picker differential fixture

The normal proof also runs `PixelPickerTests`. Pass `--pixel-picker` to run
only this engine-free fixture (no Godot, client data, shard or timings):

```
dotnet run --project tools/render_sort_tests -- --pixel-picker
```

Route build/intermediate output outside the checkout using the checkout's
normal output configuration or MSBuild `BaseIntermediateOutputPath`,
`MSBuildProjectExtensionsPath` and `OutputPath` properties.

`FrozenPixelPicker.cs` preserves the implementation at
`3560bf1f9917409a2cee448172a3f8c2a895576f`, changing only its namespace (plus
the provenance header). Original source SHA256:
`b15e84445999cf44e6021671dd4bcfc7127cf48a9e2f6e38f7e2632a20dc4145`.
Compare production against this frozen behavior, not an idealized mask oracle:
zero-coordinate, run-boundary and extraRange quirks deliberately remain.

The matrix covers missing/known IDs, negative/extreme coordinates, dimension
encodings across 127/128 and 16383/16384, transparent/opaque/alternating/long-run
and deterministic random masks, repeated Set and multiple IDs. A test-only
dictionary comparer witnesses avoided negative-coordinate lookups and unchanged
nonnegative lookup behavior. It is not a runtime counter or timing measurement.

Scope is valid masks produced by Set. Corrupt internal mask buffers may throw
while decoding dimensions before the old negative-coordinate rejection; the
new early return can change that exception order. Corrupt-buffer equivalence
and decoder repair are not claimed. No state caching, caller selection or
traversal change is involved. Live selection and performance acceptance are
separate from this fixture.
