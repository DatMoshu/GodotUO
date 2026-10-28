# ADR-0046: Bounded item-art capacity with explicit content profiles

## Status

Proposed. Owner decision required for core/network integration. The authorized
L4 experiment is an offline contract harness, not adoption of this protocol.

## Date

2026-10-28

## Engine Compatibility

Godot 4.7.2 .NET, net8.0 desktop; domain: asset addressing and networking.
The candidate uses .NET spans and explicit integer codecs, no Godot APIs. It
does not change rendering, sampling, world coordinates or exported client behavior.
Source evidence and compatibility limits: [audit](../art_capacity_audit.md).

## ADR Dependencies

ADR-0001 rendering/picking parity remains binding. L4 compile/export policy in
AGENTS.md remains binding. This proposal neither supersedes the existing protocol
nor approves Store negotiation, automatic downloads, shaders or a new render mode.
Blocks production expanded-art enablement until owner acceptance and integration
qualification. GDD linkage: existing classic-client asset fidelity; no gameplay rule changes.

## Context

Increasing an art-loader array cannot preserve item IDs through narrow game
objects, statics, multis, caches, picking, wire packets and editing tools. Existing
formats overload art bits as flags. Unannounced width changes desynchronize legacy
connections. Inference from record divisibility is ambiguous (126 bytes can be
14 Static9 records or 9 Multi14 records).
Art expansion and Z expansion are independent requirements.

## Proposed decision

Introduce a distinct nonnegative StaticArtId namespace, represented as uint in a
future client seam, with explicit checked conversion from server int. Keep land
slots at 0x4000, bodies/animations/hues/serials/multi-definition IDs independent.
For this bounded candidate, static capacity is a positive multiple of 32, at most
0x40000 slots; valid IDs are `[0,capacity)`. Archive index is checked `0x4000+id`,
at most 0x43FFF, not a new item ID. Asset presence is separately validated. Preserve
sparse holes and logical positions. Never allocate by arbitrary wire ID or uint range.

### Candidate file profile, revision 1

This is a new **lab container**, not a modified header on production MUL files.
No game file is autodetected or registered with the live format registry.
Header is 56 bytes, little endian except magic bytes:

| Offset | Width | Value |
|---|---|---|
| 0 | 4 | ASCII GAC1 |
| 4 | 2 | version 1 |
| 6 | 1 | kind: 1 Static9, 2 Multi14, 3 Multi18 |
| 7 | 1 | reserved zero |
| 8 | 4 | declared static slot capacity |
| 12 | 4 | record count, at most 4096 in this proof |
| 16 | 32 | matching content-manifest SHA-256 digest |
| 48 | 8 | exact payload byte count = count*selected stride |

Static9: uint32 art, byte local X, byte local Y (both 0..7), int8 Z, uint16 hue.
This is **art-only** widening. Static10 (int16 Z) is unsupported and requires a
different accepted format/capability; do not guess it from file length.

Multi14: uint32 component art, int16 X/Y/Z, uint32 flags (candidate 0 hidden,
1 visible). Multi18 adds a reserved uint32 zero. These are synthetic MUL-style
candidate layouts, not a claim of matching UOP attribute/flag conventions. Multi
Z already has 16 bits; world Z would still need a separate proposal. Unknown
flags/reserved fields fail closed. A future UOP profile must specify its own
compression bounds, headers, counts and attributes before use.

High Seas tiledata keeps 30-byte land rows and 41-byte static rows, four-byte
headers per 32; positional metadata extends by complete groups. The proof checks
extents only, not full metadata semantics. A production profile must require each
referenced art/metadata row to exist while permitting sparse unreferenced slots.
Keep 12-byte art/static indices only within validated offset/length limits; the
proof's art-slice budget is 64 MiB, **not a production archive-size limit**.

Validate count*stride, complete extent, nonnegative offsets, `length <= total-offset`,
record flags and every ID before allocation/publication. No partial records,
negative IDs, silent truncation, partial output or fallback to guessed legacy
formats. Prototype record arrays cap at 4096 components; no raw asset decoding.

### Fail-closed session contract

Production needs a collision-checked extension transport/opcode, authentication
context, timeout and downgrade policy decided with server maintainers. No opcode
is allocated here. Local off-by-default opt-in, revision 1, exactly ArtIds32=1,
capacity and manifest must match a local approved profile before an explicit
offer/ack activates expanded records. The harness simulates this state transition;
it does not implement the transport, handshake bytes or peer authentication.

Unknown revision/capability/format, absent local opt-in, mismatched manifest/range,
repeat negotiation or malformed expanded frame closes the simulated session
permanently. No silent retry in legacy mode with expanded content. A production
connection can remain legacy only when its entire selected content profile is
legacy-safe; otherwise refuse before world entry with an actionable explanation.

GAF1 is a **standalone complete-buffer test envelope**, big endian: magic (4),
total length uint16 (2), kind=1 equipment (1), reserved=0 (1). Then entries:
serial uint32 (zero terminates), art uint32, layer byte (1..31, unique), flags byte
(bit0 hue-present, all others zero), optional hue uint16. Terminator is exactly
four zero bytes at the end. Art 0x8000 is an ID, never a hue flag. Limit envelope
to 512 bytes and at most 32 entries (unique layers make the effective maximum 31).
Validate the entire frame before allocating/publishing output; failure closes it.
This is not 0x78, contains no mobile-body record and is never sent to a shard.

The [packet inventory](../art_capacity_audit.md#game-packet-inventory) remains the
integration checklist. All enabled carriers must get versioned handling, including
F7 nesting, targets, menus, spellbooks, effects and housing. A full container
snapshot needs explicit begin/chunk/end or atomic bounded replacement semantics;
repeated legacy 0x3C chunks can clear previously delivered entries. Bound counts
by frame size before allocation; do not change legacy packet tables globally.

### Residency and invalidation

Prototype cache is single-threaded FIFO with at most 64 entries, 1 MiB resident
bytes, 64 KiB per synthetic image, uint item keys and copy ownership. Validate
before eviction/allocation; replacements cannot accumulate stale queue entries.
Explicit reload invalidates all residents even when the profile digest is unchanged.
Production must additionally invalidate GPU atlas handles, masks, bounds, HD
negative-cache entries and dependent lighting/remaps by profile epoch. These GPU
and production loader behaviors are not proven by the CPU cache model.

## Alternatives considered

| Alternative | Benefits | Costs / disposition |
|---|---|---|
| Raise constants and remove masks globally | Small initial diff | Reject: truncates object/tool/wire paths and destroys unrelated flags/namespaces; no negotiation or bounds. |
| Versioned wide wire IDs | Direct stable IDs through game data and tools | Recommended candidate for explicit upgraded shard/client pairs; needs every enabled carrier, model/persistence migration and per-peer packet caches. This harness tests only the contract pieces. |
| Negotiated visual-ID mapping | Preserve legacy-sized wire slots with uint visual IDs in a separate manifest/map | Viable alternative for a limited visual-only profile. Map must be immutable per session/epoch, collision-free and bounded, and apply consistently to metadata/collision/target validation. 65536 wire slots cannot simultaneously represent 262144 distinct active IDs; legacy flagged families have even fewer bits. Server maps authoritatively, client cannot invent mappings. Not implemented. |
| Expand art and Z together | One coordinated migration for a shard needing both | Defer: larger movement/collision/pathfinding/packet scope. Static10 combines two changes; multi14/18 does not expand Z. |

## Migration and rollback

1. Decide operational capacity, wire strategy, transport and format ownership with
   Moshu/server/tool maintainers. Pin a profile and content manifest; qualify actual
   row availability and logical UOP indices, not just maximum constants.
2. Add adapters in Labs first; synthetic fixtures only. Migrate copies of content
   and saves with explicit version tags and reversible manifests. Do not edit the
   supplied installation or infer formats by divisibility/file extension.
3. Qualify matched client/server/editor/tool versions, including save/restart,
   high-ID static/multi/container/equipment/effect/target/housing and actual pixel
   picking. Separately test signed-byte Z limits. Only then use an isolated local
   shard under leases; no public shard experiments.
4. Legacy sessions use original codecs and unchanged content. Never broadcast a
   pre-encoded expanded packet to mixed capabilities. No automatic pack activation.
5. Roll back by disabling the seam and restoring the matching legacy content/save
   snapshot and binaries together. Refuse high-ID content that cannot be represented;
   never cast it to ushort, renumber silently or load wide saves with legacy codecs.

## Verification and limits

Run `launchers/dev/art_capacity.bat doctor`, then the launcher without arguments;
scenario: `tools/scenarios/art-capacity/README.md`. The synthetic harness tests ID
boundaries, truncation, malformed counts/offsets/flags, format/capability mismatch,
complete equipment lists and bounded residency. Run the existing Labs compile
boundary checks and full smoke before commit. Record exact results in the review run.

Off-state proof: production loader/model/network sources and project compile
conditions remain byte-identical to base; default/export builds exclude all added
Labs code. Check compiled inputs against base plus legacy smoke; this is not a new
high-ID runtime proof. No live packet, tool round trip or art rendering is claimed.
Promotion needs independent review, an Accepted ADR, a complete feature layer
contract, actual integrated cache/picking tests and matched compatibility evidence.

## Consequences

The explicit profile makes the capacity finite and keeps wider Z optional. It adds
format/manifest and per-session responsibility rather than treating a 32-bit type
as unlimited coverage. Existing clients/tools require coordinated changes. Until
those gates pass, the default client continues to support its existing namespace.
