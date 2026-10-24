# Private native rendering attribution

## Status and boundary

Diagnostic work only. The configured Godot engine and ordinary Release exports
are unchanged. The new engine is a separate copy used to investigate native
rendering cost; it is not a performance or distribution candidate. No native
GPU time or ready-versus-blocked scheduling attribution is claimed.

The elevated WPR attempt was canceled by Windows before recording. WPR reports
no active recording and the owned test client was cleaned up. No new ETL was
obtained. The old trace's lost events and missing symbols still prevent precise
CPU attribution; building symbols does not repair it.

## Reproducible private engine

The private source starts at Godot revision
`ed1daf0bf001b61586d9930840f2f1394092c079`. Its production Windows x64 Mono
template uses MSVC 14.44.35207, SDK 10.0.26100.0, single precision and the same
effective flags as the prior private build: `/O2 /MT /fp:strict /Zi /DEBUG:FULL`,
normal MSVC no-LTO and all normal renderers/features. There is no engine-wide
fast-math, feature stripping or rendering-state change.

The official template uses a different toolchain. Its `.text` section matches
the previous official exported executable exactly, while GCC/MinGW markers
distinguish it from this private MSVC build. Historical official build flags
are not fully known. Toolchain differences are provenance, not a demonstrated
explanation for the observed launch-to-launch image variation.

Version-2 diagnostic EXE SHA256:
`443ad07475421ac1123eaf13f129416b37dae59ba2b93272703a52ff01825388`.
PDB SHA256:
`2e75ffd2bbe1ac99f7eda10a86f6a8d8ad65036b079ab539573ba826d67f190c`.
Matching GUID `AFD2E985-A8A3-4992-995D-37E361E957E6`, age **3**. Original
age-1 and intermediate age-2 artifacts remain preserved. GUID alone is not
sufficient: each EXE/PDB pair's age must match. Independent DIA lookup resolves
canvas render, canvas cull and rendering-server draw names/RVAs and rejects a
deliberately wrong identity.

The copied GUO export retains managed DLL
`449854e5da5e3d3a217cd42bd0f0dcacfa8c93701217e2381f8fac87c789658b`.
All 200 files are checked; only `GUO.exe` differs from the frozen official
cover-membership export. Paths, source snapshots, exact flags and receipts stay
in private evidence, outside this repository.

## Probe semantics

Default-disabled, Windows-only RAII scopes surround the already executed native
draw, canvas update, canvas cull, RD canvas render, and the main RenderingDevice
end/execute/begin frame calls. They do not instrument each sprite or alter draw
order, floating-point settings, synchronization or rendering state. Local RD
devices are excluded. Initial configuration is process-local.

Wall mode records QPC elapsed ticks. CPU mode adds current-thread kernel/user
time using `GetThreadTimes`; 100 ns is its unit, not its effective resolution.
Zero and failed readings remain explicit. Nested scopes are inclusive and must
not be added as independent CPU shares. Elapsed minus CPU is not a breakdown
of blocked and runnable time, and present/API elapsed time is not GPU time.
Microsoft documents the distinction between thread kernel/user execution time
and elapsed-time counters in [GetThreadTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes)
and its [QPC guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).

Fixed storage allows eight thread lifetimes, 64 scopes per draw, depth 16 and
180 one-second buckets. Rows include slot, TID and thread creation identity;
caps, invalid clocks, CPU failures, unfinished scopes and boundary discards are
reported. Whole totals and per-second buckets are alternate views, not additive
measurements. A 256-empty-scope overhead check runs before the delayed window.
No file I/O occurs inside recording; a new private output file is written only
after the deadline/stop and active scopes reach zero. Parent runtime deadlines
remain necessary if the renderer stalls.

171 isolated state/RAII assertions and wall/CPU/thread-cap/deadline/delay utility
cases pass, including no-overwrite and identity joins. These are not client
timing or real 30-second coverage tests. Actual Release passes 1,207 checks.

## Pixel validation so far

Every same-client ordinary/batched comparison passes. Cross-launch disabled
versus enabled captures initially differ. An unchanged disabled repeat exactly
reproduces the known terrain-edge differences, demonstrating them without a
profiler mode change. That repeat and the official control match the enabled
capture exactly at login, forest, dungeon and docks.

Bank still differs by 12,256 raw pixels (11,344 in pixels stable across each
capture sequence). Direct image inspection shows rain in the enabled capture
and different system messages. The static-world fixture does not suppress
weather. These raw differences are retained; bank is not claimed as a strict
cross-launch zero-difference result. The profiler remains private diagnostic
code, not a renderer replacement. The live bank/docks results below follow
these separate pixel checks.

## First live result: bank

The 30-second CPU-mode window starts at 09:17:09 UTC, after the external
sampler completed and another 124.7 seconds elapsed at the requested pose.
There are 2,300 completed native draws on one identified thread lifetime,
zero caps, nesting/clock/CPU-read errors, incomplete scopes or boundary
discards. Bucket sums exactly match whole-window totals. Host CPU averages
52.8% during the recording and peaks at 93.9%; other projects remain active.

| Inclusive native scope | Calls/draw | Elapsed ms/draw | Thread CPU ms/draw |
|---|---:|---:|---:|
| Executed draw, whole outer scope | 1 | 3.524 | 3.308 |
| Canvas update | 1 | 0.001 | 0.000 |
| Canvas tree including its renderer | 8 | 2.206 | 1.963 |
| RD canvas renderer, inside the preceding row | 8 | 1.578 | 1.427 |
| RD end frame | 1 | 1.104 | 1.196 |
| Execute/present API | 1 | 0.121 | 0.109 |
| Next-frame begin | 1 | 0.035 | 0.027 |

Do not add the enclosing draw, canvas-tree and RD-canvas rows together. The
thread-CPU counter is coarse: 1,813 of 2,300 outer draws have zero CPU delta,
and the aggregate end-frame CPU estimate even exceeds its elapsed time.
Therefore the CPU column is an approximate aggregate diagnostic, not precise
per-call CPU attribution or a basis for subtracting wait time. The QPC elapsed
column is the direct scope measurement. The empty-scope check averages 0.519
microseconds; it is recorded, not subtracted from measured work.

This one private-engine observation places material elapsed work in executed
canvas/render preparation, beyond the C# submission boundary. Present and
next-frame begin are much smaller in this window. A C++ bulk submission seam
alone would still leave that canvas execution. The existing ordered GPU path
can address a wider part of this work by avoiding native canvas reconstruction,
but its own CPU capture cost and parity still need separate measurement. This
is an architectural inference from this run, not a predicted FPS gain.

The preceding 30-second managed sampler measured 67.746 FPS; the native window
contains 76.667 completed native draws per planned second. They are different
windows and must not be reported as simultaneous FPS/phase measurements.

## Docks and wall-only bank control

Docks records 2,672 completed native draws in 30 seconds from 09:20:40 UTC.
Setup/sample completion precedes the window by 125.0 seconds. Identity, caps,
nesting, clock, CPU-read, incomplete and boundary checks all pass; bucket sums
match. Host CPU averages 57.9%, peaking at 87.2% in the recording window.

| Inclusive native scope | Docks elapsed ms/draw | Docks thread CPU ms/draw | Bank wall-only elapsed ms/draw |
|---|---:|---:|---:|
| Executed draw, whole outer scope | 3.192 | 3.076 | 2.905 |
| Canvas update | 0.001 | 0.000 | 0.0002 |
| Canvas tree including its renderer | 1.944 | 1.813 | 1.846 |
| RD canvas renderer, inside the preceding row | 1.398 | 1.374 | 1.274 |
| RD end frame | 1.051 | 1.058 | 0.896 |
| Execute/present API | 0.121 | 0.123 | 0.100 |
| Next-frame begin | 0.020 | 0.018 | 0.024 |

The wall-only bank control records 2,952 completed native draws from 09:24:12
UTC with the same complete coverage checks and no per-scope CPU calls. Its
host CPU averages 30.5%, peaking at 82.6%, and the empty-scope overhead check
averages 0.054 microseconds (docks CPU mode: 0.615). The lower elapsed result
cannot be attributed solely to removing CPU queries because host load and live
work differ. It still puts material work in canvas execution and end-frame
processing, supporting the same next investigation without treating the probe
as free. Both runs again execute eight canvas calls per native draw.

The separate pre-native samples are docks 98.091 FPS and bank wall-mode 78.461;
completed native draws per planned recording second are 89.067 and 98.400.
These are neither aligned comparisons nor ordinary official-engine benchmarks.
No CUO client was launched. The inherited configuration helper's CUO-named log
message refers only to the shared settings format written for GUO.

The remaining question is which canvas traversal/batch-building/end-frame
operations dominate inside those scopes. The current hooks do not resolve
per-viewport cost or isolate the C# call-admission boundary. Choose a smaller
diagnostic or controlled submission experiment before a production engine fork.

## Source review and next diagnostic

A separate visible GUO worker independently reconciled the bank raw stage totals,
per-second buckets and thread identity: 48,300 completed scopes, no reported
coverage errors. It reviewed the docks and wall-only summaries, without claiming
an independent raw-file rejoin for those two. No concrete capture-invalidating
defect was found in the reviewed helper; this does not establish process-wide
coverage or eliminate probe overhead.

Pinned engine source shows retained canvas commands are still decoded into
native instances and batches on each draw. The end-frame scope includes transfer
handling, draw-graph dependency preparation/barriers/replay, possible secondary
task waits, and driver command-buffer finalization. It is not synonymous with
waiting for the GPU. A follow-up private wall-only diagnostic will split these
operations, with bounded item/batch accounting and unchanged rendering behavior.
That follow-up has not yet produced runtime measurements.

The editor chooses its Debug GodotSharp API at runtime even though GUO compiles
against the NuGet assembly matching the shipped Release API. Optimizing GUO's
own Debug/Labs assembly does not optimize those loaded bindings. A separate
private-copy experiment is being prepared with normal and optimized Debug API
sets, preserving DEBUG semantics and the Labs export boundary. It needs loaded
assembly identity, pixel/failure checks and matched samples; no result or engine
promotion is claimed here.
