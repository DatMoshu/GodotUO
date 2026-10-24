# Typed UI texture commands

This ordinary-client allocation change replaces three simple queued texture
closures with typed records. It does not enable retained canvas or either GPU
prototype. The owner requested GUO-only iteration; no new CUO run was made.

## Behavior and scope

`GumpPic`, the background of `Button`, and `GumpPicTiled` keep their existing
texture-selection, missing-texture and base-control behavior. Their texture,
source rectangle, XY, hue and depth are captured when queued, exactly as before.
Width and height remain live reads from the control when flushed, including a
disposed control. Button captions keep their following queue position.

Each texture record occupies its original position in the mixed text/callback
queue. A lazy side list stores the payload; no sorting or additional draw pass
is introduced. Text and callback references distinguish existing entries from
the two texture tags. The original Draw/DrawTiled overloads are used. A normal
return counts one draw even if zero or negative geometry emits nothing.
Callbacks retain their effects, exception order and return-count semantics.

Clear releases payload references and retains capacity. Flushing does not clear
the list, so a caller can flush it again. There is no global pool or extra
disposal/validity guard. All other UI callbacks are unchanged. These are marked
L0 port deviations for the approved performance work, not a new client option.

## Storage and allocation checks

The actual x64 Release probe measures the old and current mixed queue element at
40 bytes each; the side payload is 48 bytes. The RenderLists object gains one
nullable reference; its complete object padding is not measured. Lists without
these textures do not allocate the side list. A warmed list queues 32,000 typed
commands without allocating.

Fresh child lists cannot amortize array growth. The probe deliberately reports
unfavorable populations too:

| Textures per fresh list | Legacy bytes / 100 lists | Typed bytes / 100 lists |
|---:|---:|---:|
| 0 | 31,200 | 31,200 |
| 1 | 63,200 | 60,000 |
| 2 | 76,800 | 72,000 |
| 3 | 90,400 | 93,600 |
| 8 | 192,800 | 168,800 |
| 20 | 552,800 | 600,800 |
| 32 | 716,000 | 600,800 |

Both fixture paths use the patched containing object, so this table isolates
queue construction and excludes the new object reference. Counts 3 and 20
regress in this fixture. Do not claim every UI arrangement allocates less.

## Actual Release measurement

Control DLL SHA-256:
`39f428555a5a4828c003ed6a2acfa6102cf53d01382978ba709aec400d992109`.
Candidate DLL SHA-256:
`1a56be654750e96df299559b1aaa8da916ee656e5f775fecc042c9062bf137a4`.
Both are frozen actual Release exports. Standing live samples discard 60 seconds
at the location, then measure 30 seconds of actual draw intervals at 2560×1440,
zoom 2, cap/vsync off. Retained canvas, GPU experiments and PostFX are off.
Control order is bank/docks; candidate order is docks/bank.

| Scene | Before / after FPS | p50 ms | p95 ms | p99 ms | Allocated KB/frame |
|---|---:|---:|---:|---:|---:|
| Bank | 76.627 / 67.855 | 12.718 / 14.465 | 18.333 / 19.878 | 19.951 / 21.226 | 14.1 / 5.9 |
| Docks | 103.247 / 92.428 | 9.189 / 10.410 | 14.828 / 16.675 | 17.860 / 18.944 | 14.0 / 5.8 |

Bank draws: 2,299 / 2,036 over 30.002 / 30.005 seconds. Docks: 3,098 / 2,773
over 30.006 / 30.002 seconds. Main-thread allocated bytes per actual draw fall
from 14,394.993 to 6,004.483 at bank and 14,317.857 to 5,891.799 at docks.
That is approximately 58% less in these scenes. This is allocation evidence;
FPS and frame-time tails are worse in both after samples.

World preparation averages are bank 5.379 / 6.036 ms and docks 3.717 / 4.070 ms;
world draw is 4.038 / 4.759 ms and 2.899 / 3.436 ms. UI time is 0.057 / 0.059 ms
and 0.050 / 0.061 ms. No measured UI or whole-frame speedup is established.
Whole-run host CPU means, including startup/warmup, are bank 44.4% / 50.8% and
docks 42.1% / 39.3%, with maxima 97.5% / 97.2% and 98.7% / 99.6%.
Atlas updates during timing are bank 4 / 15 and docks 1 / 12. Other projects
remain active; unequal shared load and uploads prevent a causal FPS conclusion.

## Allocation attribution

Separate 30-second gc-verbose traces contain bank 280 / 131 and docks 439 / 160
weighted allocation samples. Weighted sampled bytes total 29,848,816 / 13,937,120
and 46,798,216 / 17,056,048. All four traces report zero lost events and zero
missing allocation stacks. These are samples, not an exact object census;
different frame counts also prevent treating raw totals as a per-frame ratio.

| First GUO allocation site | Bank before / after weighted bytes | Docks before / after weighted bytes |
|---|---:|---:|
| GumpPic.AddToRenderLists | 7,355,400 / 0 | 11,726,000 / 0 |
| Button.AddToRenderLists | 6,396,816 / 0 | 11,299,600 / 0 |
| GumpPicTiled.AddToRenderLists | 4,050,800 / 0 | 6,929,000 / 0 |

UI drawing still accounts for 10,532,408 bank / 13,218,448 docks sampled bytes
afterward. Fresh child RenderLists and their lists, remaining ResizePic/HTML
callbacks, paperdoll updates and profiler bookkeeping remain. Inlining places
some child-list allocation directly under DrawGumps; this is not evidence that
the typed texture draw itself allocates. A broad parent temporary-file search
overlapped part of the baseline trace work; those traces are diagnostic only.
Their preceding short samples are not the standing FPS cohort above. Raw traces
stay private because trace metadata can contain command-line credentials.

## Parity and validation

Actual Release passes 996 checks, including 56 new UI checks. Optimized
Debug/Labs passes 1,336. The fixtures exercise mixed text/callback/texture order,
live resizing and disposal, captured UV/hue/position, partial tiling, nested
clipping and transforms, child lists, repeat flushing, strong references until
Clear, callback exceptions and true/false return semantics. Real Button state
selection is source-reviewed and unchanged, rather than separately driven by
the fixture.

Three plain captures per build are stable and cross-build identical: all
307,200 login pixels and all 3,686,400 pixels in each bank, forest, dungeon and
docks fixture match. Candidate retained-canvas off/on also matches exactly in
all four world fixtures with retention exercised. Each capture waits for two
actual draws and two post-draw fences. The control's in-run toggle check targets
the configured renderer, whereas the candidate's targets retained canvas;
cross-build comparisons use plain frames, not those different toggle timings.
These checks establish captured-scene parity, not every gameplay/device state.

Full smoke exits successfully with aggregate `OK`, while the inherited editor
`splat_lods_present` assertion still fails. That issue remains unresolved.
Docs lint (323 documents), 114 launcher pairs, privacy and whitespace checks
pass. Owned sampling clients have cleanup receipts. The inherited probe exit
warnings for a CanvasItem RID and an ObjectDB instance remain unchanged.
This remains a local review candidate, with no push, merge or default promotion.

Evidence run:
`20261024_guo-codex-perf-implementation-20261022_ui-texture-commands`.
