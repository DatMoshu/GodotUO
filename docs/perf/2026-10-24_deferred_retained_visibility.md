# Deferred retained-canvas visibility

The existing `GUO_RETAIN_CANVAS=1` experiment can keep reused canvas items
visible between complete main frames. Previously it hid and revealed every
retained command owner each frame, even when the frame was unchanged. The
experiment remains off by default and independent of both GPU paths.

## Implementation and limits

`GameController.DrawFrame` opts into a frame boundary that ends after the final
composite. `BeginFrame` continues clearing raw and uncached owners. Successful
`EndFrame` hides only unused retained owners; used owners retain visibility.
The default boundary for embedded/probe callers remains eager. Legacy GPU and
ordered RenderingDevice capture retain their existing ownership behavior.

An interrupted main draw abandons pending raw/cover commands, clears and hides
the pool, resets retained streams and clears the separately owned ID mirror.
An interop cleanup exception is recorded without replacing the original draw
exception; the next frame retries cleanup before opening. An unclosed batch
at successful finalization is rejected in Release too. This is synchronous
main-thread recovery, not rollback of external clear hosts, PostFX resources or
GPU work. Native errors that only log rather than throw are not automatically
detected by this managed exception policy.

The new transition tests also exposed a pre-existing retention clipping bug.
When a pooled sprite owner became an empty clip ancestor, ending its old
command stream could clear it *after* clipping had been enabled. Godot's
`CanvasItemClear` resets the native clip flag. `ClipBegin` now finishes the old
stream before enabling clipping. Both eager and deferred retention failed the
original transition test; the reordered operations pass it. See Godot's
[canvas item clear implementation](https://github.com/godotengine/godot/blob/ed1daf0bf/servers/rendering/renderer_canvas_render.h).

No shader, texture filtering, world order, roof rule, lighting rule, picking
decision or default profile setting changes. The added finalizer uses the
existing pool and introduces no per-frame managed collection.

## Validation completed

- 940 actual Release checks, repeated on final v2; 1,280 optimized Debug/Labs checks.
- Exact raw/eager/deferred pixels for shrink/grow/empty frames, clip and
  transform ancestors, custom blend, raw triangle fallback, live texture
  changes and oversized-command recovery.
- Original draw exceptions preserved; aborted partial frames empty; subsequent
  complete frames match the reference; missing finalizers recover.
- Target order reversal, an omitted pass, target replacement with a different
  size, transforms/clipping and final composite match on every source target
  and the final surface.
- Stable synthetic retained frames make zero redundant hide/reveal calls.
- Four full-size world scenes and login have zero same-client off/on pixel
  differences, including all 3,686,400 pixels of each world capture.

These checks do not constitute complete live scene-transition, device, death
screen or every roof/picking acceptance. No claim that the old native profiling
trace establishes exact submission CPU time is made here.

Full smoke reports aggregate `OK`, retaining the inherited editor
`splat_lods_present` assertion failure. Docs, launcher, privacy and whitespace
checks pass. The inherited probe-exit CanvasItem/ObjectDB warning also remains;
it was present in the preceding allocation candidate.
Independent source review found no new concrete defect in the frame boundary,
fallback, clipping or target lifetime changes. It did not run independent
pixels or inject native cleanup failures; those remain explicit coverage limits.

## Measurements

Live Release standing samples use 60 seconds settling followed by
30 seconds measurement, 2560x1440, zoom 2, classic/plain rendering, PostFX off
and cap/vsync disabled. Compare the previous retained implementation, this
candidate with retention enabled, and this candidate with retention disabled.
Bank/docks order is reversed. Other projects remained active throughout.

| Scene / mode | FPS | p50 / p95 / p99 ms | Prepare / world draw ms | KB/frame | Host CPU mean / max % |
|---|---:|---:|---:|---:|---:|
| Bank, previous retention | 74.292 | 12.976 / 18.607 / 19.673 | 5.576 / 3.885 | 14.2 | 56.7 / 98.7 |
| Bank, deferred retention | 102.272 | 9.524 / 14.202 / 16.935 | 4.177 / 2.713 | 14.3 | 45.8 / 92.3 |
| Bank, candidate retention off | 83.022 | 11.358 / 18.446 / 20.079 | 4.895 / 3.700 | 14.1 | 39.4 / 97.3 |
| Docks, previous retention | 90.789 | 10.604 / 16.290 / 17.929 | 4.410 / 3.150 | 14.3 | 57.1 / 95.0 |
| Docks, deferred retention | 98.302 | 9.509 / 15.898 / 17.851 | 4.058 / 2.743 | 14.2 | 41.2 / 94.2 |
| Docks, candidate retention off | 98.347 | 9.720 / 15.896 / 17.285 | 3.918 / 3.038 | 14.0 | 44.7 / 95.2 |

Host CPU statistics cover startup, settling and measurement together. They
cannot be used to normalize the 30-second FPS samples. These are shared-load
observations, not causal speedup percentages or evidence of reaching 250 FPS.
In particular docks shows essentially unchanged whole-frame FPS against the
candidate's ordinary off mode, despite reduced submission work.

| Scene / retention | Hides + reveals / draw | Owner clears / draw | Submitted retained records / draw | Prefix matches / draw | Atlas updates |
|---|---:|---:|---:|---:|---:|
| Bank, previous | 3,394.56 | 24.72 | 1,757.06 | 12,749.23 | 12 |
| Bank, deferred | 0 | 21.17 | 103.33 | 13,831.92 | 10 |
| Docks, previous | 3,280.67 | 27.80 | 164.81 | 12,346.18 | 11 |
| Docks, deferred | 0.000339 | 24.97 | 123.97 | 12,396.04 | 13 |

The new docks window made one reveal in total across 2,950 draws; bank made
none across 3,069 draws. This confirms the intended visibility-call reduction.
Command-stream stability, NPC/animation work and atlas uploads differed between
launches, especially at bank. The much lower submitted-record count is therefore
not assigned solely to this patch. Off-mode retained counters are zero because
that path is not instrumented by the retained-command counters; it still draws.

The same 46-tile bank/docks routes also completed with every requested movement
acknowledged and no pending request. These use 60 seconds settling and a 250 ms
pause between completed movement requests; they are controlled interrupted
travel, not continuous running. Route hashes and point lists match exactly.

| Route / retention | FPS | p50 / p95 / p99 ms | Prepare / draw ms | KB/frame | Hides + reveals / draw | Atlas updates |
|---|---:|---:|---:|---:|---:|---:|
| Bank, previous | 60.051 | 16.286 / 22.896 / 25.805 | 6.339 / 5.614 | 33.5 | 3,176.04 | 83 |
| Bank, deferred | 69.401 | 13.843 / 20.515 / 23.892 | 5.820 / 4.614 | 30.8 | 0.453 | 37 |
| Docks, previous | 90.276 | 10.663 / 16.585 / 20.228 | 4.119 / 3.455 | 21.9 | 3,162.32 | 69 |
| Docks, deferred | 91.461 | 10.379 / 16.786 / 19.696 | 4.046 / 3.368 | 21.1 | 0.312 | 43 |

Bank before/after measured 1,367/1,557 draws over 22.764/22.435 seconds;
docks measured 2,137/2,156 over 23.672/23.573 seconds. Bank had 50 requests
and acknowledgements, docks 55, including turns. Whole-run host CPU mean/max
was bank 47.6/93.6% before and 45.8/91.6% after; docks 36.1/86.3% and
36.0/77.9%. Upload counts and command streams still differ. Docks p95 slightly
worsened despite fewer visibility calls. These data establish call reduction,
not isolated FPS or allocation gains. Labels are `deferred-travel-bank-before`,
`deferred-travel-bank-after`, `deferred-travel-docks-before` and
`deferred-travel-docks-after`.

The measured v1 candidate Release DLL SHA-256 is
`5e7ef0835dc18c92154592c389ab9ae9b0bccf5161bd106b21d9326d9e30af64`.
The final v2 restores the original public no-argument `BeginFrame()` CLR
signature as a forwarding overload. The main frame already calls the explicit
boolean overload, so its measured implementation is unchanged. V2 DLL SHA-256:
`39f428555a5a4828c003ed6a2acfa6102cf53d01382978ba709aec400d992109`.
Control is the preceding incidental-allocation candidate, DLL SHA-256
`71ef23dcf2bb5014b41150f5c60ef2f0db7080b231de1d978acd0b16a5ee30fe`.

Evidence run: `20261024_guo-codex-perf-implementation-20261022_deferred-retained-visibility`.
Validation labels: `deferred-visibility-release-v1`, `deferred-visibility-release-v2`,
`deferred-targets-labs-v1`. Repository checks: `deferred-final-checks`.
Pixel label: `deferred-visibility-pixels-v1`.
Initial failing fixtures are retained as `deferred-visibility-debug-v1` and
`deferred-visibility-debug-v2`, followed by `deferred-visibility-clipfix-debug-v1`.
