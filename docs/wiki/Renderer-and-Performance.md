# Renderer and performance

This page describes selection at `fc9e3588` (2026-10-28). It makes no new
benchmark claim. Renderer implementations, defaults and historical timing
results must be kept separate.

## Production selection

| Selection | Behaviour |
|---|---|
| Normal play with a supported rendering method | Depth World is selected by default; each frame still needs capability admission |
| `--canvas-world` | Requests the ordered canvas world path from the start |
| Godot Compatibility (`gl_compatibility`) | Selects canvas; Depth World is disabled |
| Batcher probe | Keeps explicit backend selection; `--depth-world` opts the probe into Depth World |
| Failure during admitted Depth World rendering | The capture retains a complete ordered canvas source for fallback |

Sources: [DepthWorldProbe](../../godot/GUO/src/Render/DepthWorldProbe.cs),
[ordered capture](../../godot/GUO/src/Render/DepthWorld/OrderedWorldCapture.cs),
[batcher integration](../../godot/GUO/src/Render/DepthWorld/UltimaBatcher2D.OrderedWorld.cs).
Do not interpret the word “Probe” in the class name as the current product status.

The shared world traversal, depth/order contract and picking remain critical.
A correct-looking empty scene does not prove roofs, mobiles, doors, blends,
atlas uploads or targeting. See [Architecture](Architecture.md),
[Parity and Drift](Parity-and-Drift.md) and the [performance gate](../perf/performance_gate.md).

## Recover with canvas

From a source checkout:

```bat
launchers\game\play.bat --canvas-world
```

For an exported client, put user flags after Godot's separator:

```bat
GUO.exe -- --play --canvas-world --client-data "<your UO folder>"
```

Use the same shard, location, viewport and profile for a meaningful visual
comparison. The startup log reports the chosen world renderer. Preserve the
previous export/profile so you can return to it. See [Install and Update](Install-and-Update.md).

## Camera range

Depth World defaults the maximum zoom-out range to 5; canvas retains the
upstream range unless explicitly overridden. `--zoom-max=N` supplies a custom
maximum. This is a limit, not the startup zoom or a measured performance target.
The profile's base `DefaultScale` initializer is 0.7; saved values and platform
migrations matter. Profile load/save caps retained zoom above 2.5 for the next login.
Sources: [renderer selection](../../godot/GUO/src/Render/DepthWorldProbe.cs),
[profile](../../godot/GUO/src/Configuration/Profile.cs).

## Presentation options

[Gameplay and Settings](Gameplay-and-Settings.md) lists current opt-ins.
Styled water, HD art, lights, ambient effects and screen effects can change
the appearance and cost of a frame. Record them when reporting a problem.
Do not promote internal `GUO_PERF_*`, retention, residency or cache experiments
into recommended player settings. Some switches are diagnostic or Labs-only.
Their source inventory is in [Feature Coverage](Feature-Coverage.md).

## Reading performance evidence

[Performance](Performance.md) explains the existing lab and its report fields.
Historical documents preserve their original build, hardware and method;
they do not establish the current build's FPS. Compare actual Release exports
at matching location, camera, viewport, effects, renderer and data, and report
mean/p95/p99 frame time plus host load. Debug, Labs and private-engine results
belong in separate cohorts. Draw counts and compile success are not speedup proof.

If you only need a correctness screenshot, do not run a benchmark. A still can
show layout or a rendering defect, but cannot prove animation smoothness or FPS.
