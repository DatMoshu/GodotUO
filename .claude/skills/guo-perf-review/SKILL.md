---
name: guo-perf-review
description: Diagnose GUO client or editor performance with matched measurements of frame latency, allocations, caches, IO, GPU work and idle cost while preserving ClassicUO parity. Use for slow paths, optimization reviews or performance regressions.
---

# GUO performance review

Read [the review contract](../../../docs/review/contract.md),
`docs/perf/performance_gate.md`, and the README of the selected probe under
`tools/perf_probe`, `tools/perf_lab`, `tools/perf_dump` or `tools/perf_tour`.
Read current accepted renderer ADRs for rendering work; do not infer the active
renderer from the original port plan alone.

Start with the symptom and a reproducible workload: crowded world, travel, zoom,
login, asset search, brush drag, tab switch, animation playback, import or idle.
Trace CPU preparation, GPU submission, IO, decode, allocation and synchronization
separately. Source inspection identifies candidates; it cannot establish a speedup.

Keep baseline and candidate hardware, build configuration, renderer, resolution,
zoom, fixture, data, profile, cache state and probe flags matched. Record warm-up,
sample duration, repetitions and other host load. Measure cold and warm paths as
different experiments. Do not run competing benchmarks simultaneously.

Report frame/operation p50, p95 and p99 latency, allocation rate/GC pauses, retained
memory, draw calls, batch items and cache occupancy where relevant. Use existing
telemetry; account for observer overhead. Report noise and run-to-run spread rather
than selecting the fastest run. Do not turn historical measurements into targets.

The existing performance gate binds draw-call and batch-item counts, not frame-time
improvement. Its quiet static-world fixture omits real workload behavior. Complement
it with a representative live scenario for packets, mobiles, effects and interaction.
Editor responsiveness needs a separate experiment from an exported client.

Inspect bounded cache growth and eviction, invalidation correctness, hidden-panel
processing, texture/RID disposal, loader contention, main-thread waits and repeated
reloads. For any optimization, verify pixels, picking, packet/order semantics and
resource lifetime as applicable. A faster incorrect scene is a failed result.

Finish with the measured bottleneck, proposed smallest change, matched before/after
evidence, parity proof and rollback. If no runnable fixture exists, return hypotheses
and a measurement plan explicitly labeled unmeasured.
