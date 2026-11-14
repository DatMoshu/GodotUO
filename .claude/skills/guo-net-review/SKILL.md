---
name: guo-net-review
description: Deep review of GUO packet parsing, login, transport, movement reconciliation, plugin dispatch and shard/admin boundaries against ClassicUO behavior. Use for networking correctness, security boundaries and latency investigations.
---

# GUO netcode review

Read [the review contract](../../../docs/review/contract.md), the network role,
applicable accepted ADRs, `tools/network_tests/README.md` and the relevant data
contract in `docs/data_formats.md`. Start at `godot/GUO/src/Network`; follow consumers
in Game and platform transports. Preserve the existing UO wire protocol. Generic
multiplayer advice is not permission to add packet versioning or host migration.

Trace connection -> seed/version -> login -> redirect -> character selection ->
world entry -> gameplay -> disconnect/reconnect. Record state owners, buffered data
and reset points. Inspect compression/encryption transitions and credential logging.

For packet parsing, inspect lengths before reads, endianness, signedness, fixed strings,
partial headers/payloads, ring-buffer wrap, concatenated packets and unknown IDs.
Prove forward progress or bounded waiting with truncated/invalid lengths. Inspect
decompression bounds, queued-send ownership, backpressure and reconnect cleanup.

For movement and gameplay, trace sequence/ack/rejection/resync through prediction,
world state and UI. Do not count a locally echoed command as a successful server
round trip. For plugins, check mutation, veto and injection order, callback lifetime,
ABI layout and failure isolation. Keep client, admin channel, editor MCP and shard
permissions distinct; loopback alone is not authorization.

Prefer the engine-independent network tests and the production loopback diagnostics
probe first. The README distinguishes a copied parse-loop test from production
socket coverage: a passing copy cannot alone prove the real parser. Add relevant
server-lab scenarios against a controlled shard when needed. Use synthetic payloads
and redact credentials, account details and private traffic from evidence.

Report which transport, client version, server/backend and plugin configuration were
exercised. Treat latency, jitter, reconnect and malformed traffic as separate cases.
No live-server or cross-platform claim follows from loopback fixtures alone.
