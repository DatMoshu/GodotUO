# ADR-0055: Bounded neighboring-cell reads for authored PZ buildings

## Status

Accepted by the owner, 2026-11-23 (proposed 2026-11-15). Tools-layer design; the
implementation merged to integration on 2026-11-23. No distribution or
released-workflow readiness is claimed. Independent of ADR-0052 role classification, while interoperating with
its explicit source-object inventory.

## Problem and scope

The existing adapter refuses any selected building whose authored rectangle plus
its two-source-cell context margin crosses a256-cell source boundary. A primary
header can describe the complete room geometry across that boundary, while the
neighboring lotpack stores some of its actual tiles. Refusing missing neighbors
is correct; refusing available and verifiable neighbors prevents faithful import.

Read the complete selected building declared by the primary header. Never merge
neighbor buildings by name, proximity, matching room numbers or apparent geometry.
Neighbor headers supply their own tile dictionaries and pack format metadata;
primary room membership remains authoritative. This is source data ingestion,
not a new renderer, native map writer, room generator or runtime behavior.

## Portable interface and decision

Add an explicit opt-in `--neighbor-cells` to `zomboid-resolve`, forwarded as
`resolve(..., allow_neighbor_cells=False)`. Existing calls retain their refusal
at source boundaries. Single-cell results and geometry remain unchanged even
when the opt-in is supplied.

An internal bounded window reader receives the primary header and source bounds
`[x0,y0,x1,y1]`, with coordinates relative to that header's cell. A cross-cell
window may span at most256 source squares per axis and four source cells. Larger
windows fail explicitly. Do not silently crop, wrap, replace with empty tiles,
fall back to primary dictionary indices or infer missing neighboring data.

Load each required header/pack pair using its own existing decoder. Translate
chunk observations into the primary cell's continuous coordinate frame before
wall-edge, object-group and staircase analysis. Require complete referenced files,
valid known versions and dimensions, correct declared cell identity, and unique
translated positions. Missing or malformed neighbors fail before native output.
Preserve N/W edge orientation, source stack order, levels and source tile IDs.
Existing single-cell decoding and its accepted source bounds are not tightened.

For cross-cell layouts only, provenance adds
`source_window_contract: "pz-cell-window/1"`, `source_window_bounds`, and ordered
`source_cells`. Each cell record contains `cell:[cx,cy]`, `header`,
`header_sha256`, `pack`, and `pack_sha256`. Include the primary pair and every
neighbor read. The legacy primary fields remain and must agree exactly with its
cell record. Hash every consumed pair and retain the source definition hashes.

Existing source coordinates in authored rectangles stay primary-cell-relative.
Cell lineage uses actual source `cell` and local `at`, with `frame_at` and
`room_header` preserving the primary-frame position and room-ID namespace.
Cross-cell furnishing observations retain continuous primary-frame `at` for
object grouping and add actual `source_cell` and `source_at`. Document this
coordinate distinction in section32 before emitting it. Source-object masks and
native layout coordinates remain in the same target frame as before.

The complete provenance enters existing snapshot/profile/instance identities.
Do not reuse a single-cell source profile for a multi-cell read. A downstream native
consumer must verify every declared pair against its completed source manifest,
reject duplicates, unsupported contracts or a primary mismatch, and refuse any
changed neighbor before overlay export. This extends provenance verification;
it does not weaken native geometry, material, access or gameplay checks.

## Acceptance

1. Original binary fixtures exercise horizontal and vertical seams, including
   negative primary-relative context and distinct per-header tile dictionaries.
2. Missing header/pack, malformed version, wrong cell identity, excessive bounds
   and duplicate translated source observations refuse without partial output.
3. Source object masks and N/W doors remain continuous across the seam; unrelated
   neighboring rooms never become part of the selected building.
4. Single-cell layouts retain their semantic and native geometry hashes/bytes
   under the same theme; converter profile identity still changes with code.
5. A real previously refused cross-cell building preserves its complete authored
   room masks and source tiles against an independent multi-cell reader. Record
   any remaining stair/furnishing/native blockers; source resolution is not a
   claim of a native-valid or walkable building.
6. Native source-manifest binding checks every cell, catches neighbor tampering,
   and keeps legacy single-cell bindings unchanged. Run affected adapter and
   original-fixture suites. Runtime traversal remains separate evidence.

## Alternatives and limits

Cropping loses rooms; dictionary reuse corrupts source tile identity; flattening
or arbitrary replacement geometry hides the defect. None is acceptable.
Unbounded district extraction and deduplicating buildings across header records
are separate proposals. Source data remains local and read-only; no proprietary
fixtures enter the repository. Implementation/review can be separated from the
source-object feature before any future PR; no commit or push is authorized here.
