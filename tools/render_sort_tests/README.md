# render_sort_tests

Proves that the sorted world pass's key sort (`DepthKey`, used by
`RenderLists.DrawWorld`) orders draws exactly as the comparison sort it
replaced (depth, then queue order), and measures the difference.

```
dotnet run --project tools/render_sort_tests            # proof; exit 1 on a mismatch
dotnet run --project tools/render_sort_tests -- --bench # proof, then timings
```

The proof runs five input shapes (spread depths of both signs, piles of
ties, signed zeros, a nearly sorted frame, extreme magnitudes) at sizes up
to 40,000 draws. `launchers/dev/smoke.bat` runs the proof. Timings are
advisory and need no client data, shard or Godot.
