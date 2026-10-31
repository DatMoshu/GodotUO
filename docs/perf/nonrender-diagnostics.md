# Non-render timing boundaries

The existing perf probe enables `PerfStages` only during its sampling window.
The added stages are CPU elapsed milliseconds per sampled process frame,
accumulated across updates between samples. They do not change update frequency,
packet ordering, buffers, plugin callbacks, or rendering.

| JSON field | Work timed |
|---|---|
| `stage_network_receive_ms` | Socket read, decrypt and decompress, inclusive |
| `stage_network_decompress_ms` | Huffman decompression, inside receive |
| `stage_network_parse_ms` | Framing, receive plugins and dispatch, inclusive |
| `stage_plugin_receive_ms` | Receive plugin callbacks, inside parse |
| `stage_network_dispatch_ms` | Accepted packet handlers/mod dispatch, inside parse |
| `stage_network_flush_ms` | Queued socket sends plus statistics update |
| `stage_plugin_tick_ms` | Plugin tick callbacks |
| `stage_ui_update_ms` | UI update outside world simulation |

Do not sum nested stages with their parents. `update_world_ms` still describes
only the existing scene-update profiler context. These timings are not GPU time,
network transit latency, or queue residence time. A normal idle local-shard run
cannot establish the absence of connect/backpressure stalls.

The instrumentation is off by default and has no per-call heap allocation.
Disabled calls branch without reading the clock. Enabled inner scopes record
time even if their callback throws; outer stage totals describe completed update
boundaries only. Existing probe JSON/Markdown stage enumeration includes the new
fields automatically. Main-thread use only, like the existing `PerfStages` data.

Validation required before acceptance: release build and smoke; matched enabled
and disabled runs to measure observer cost; login, plugin receive veto/mutation,
plugin-injected packets, world traffic, reconnect and send bursts. Queue ages,
packet rates, cold-start instrumentation and UI/text workloads remain separate
work. No speedup or runtime parity claim follows from adding these counters.

`tools/network_tests` links the production `PerfStages` and checks disabled
scopes, gate transitions, exception unwinding, nested inclusive totals and
`Take` reset/export behavior without the engine. These contracts do not prove
live socket/plugin parity or establish observer overhead. The decompression
scope ends before failure handling, so disconnect callbacks belong to receive
time rather than Huffman decode time.
