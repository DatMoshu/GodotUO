# The world drawn through the depth pass (GPU-01, GPU-02)

`--gpu-world` (ADR-0007, amendment 2026-10-19, implemented 2026-10-22) keeps
each chunk's eligible statics as instances of the world depth pass and
captures the sorted pass's sprites into it, so the GPU depth test orders
everything by the 28-bit key `WorldDepthKey` builds from `CalculateDepthZ()`,
the tile's walk position and its chain slot. Off by default; Forward+ only.
The ADR's "Implemented 2026-10-22" subsection records the design, the
deviations and the per-frame fallbacks.

## What is measured so far

`launchers\dev\depth_probe.bat`, Forward+, RTX 4090, 2026-10-22: **46/46**.

| Check | Result |
|---|---|
| Pixel parity, 11 scales x 3 origins, sprite shaders restructured for the pass | 0 px |
| Adjacent 24-bit keys, bottom / middle / top, both orders, one and two pages | ordered |
| Exact 28-bit instance upload and the exhaustive adjacent-key sweep | 0 wrong of 268,435,455 pairs x 4 |
| All ties, one page: later wins (the compare passes a tie) | yes |
| All ties, two pages, both orders: the later holder wins (ordered pass) | yes |
| All ties, three across two pages, both orders: the last holder wins | yes |

The CPU tests (`tools/world_depth_tests`, 623 checks) cover the key's
exactness over the whole depth domain, the tile order against the walk, the
slot interleaving, the frame-relative bounds and both plans, page-aware and
not.

## What is not measured yet

- Parity on the gate scenes, plain against `--gpu-world`, through
  `perf_probe --perf-parity`: 0 strict pixels outside the labels and the
  radar is the gate.
- Frame time: the bank statics walk, sort and draw (about 14 ms on the
  perf gate) should drop by more than half; no number is claimed until the
  probe runs in a granted window.
- The Compatibility renderer: the switch stays off there (ADR-0007).

## Running it

```
launchers\game\play.bat --gpu-world
launchers\dev\depth_probe.bat
cd tools\world_depth_tests && dotnet run -c Release
```

The client logs each reason a frame stayed on the canvas once
(`[GUO] --gpu-world: frame drawn on the canvas: ...`) and counts them in
`GpuWorld.FramesFallback`.
