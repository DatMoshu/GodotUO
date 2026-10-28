# ADR-0046: Map paging in Labs — stable page facets, bounded residency

## Status

**Proposed** (draft, 2026-10-28). Milestone 1 of the paging work, an L4 Labs
experiment. Not accepted: the core/network decision, any default-on behaviour,
public push or PR, publication and every server or protocol change beyond a
local fixture still need Moshu.

## Context

The planned source-derived world is 48 pages of 7168 × 4096 tiles (facet audit
`20261026_codex-multi-parcel_census/data/facet-audit-v1`). A two-page proof
(retained commit `8a83414d`, run `page-travel-proof-v7`) crossed between pages
that replaced stock facets 0 and 1, with no client change. That does not scale:
the stock client has six facet slots, falls back to facet 0 for any facet it
has no files for, and UltimaLive's wider loader opens and indexes every
announced map at once. One page's block index is about 25 MiB of managed
memory (458,752 blocks); all 48 would be over 1 GiB before any other client
resource.

## Decision

1. **Identity is separate from residency.** Each page has a permanent logical
   ID mapped to one native facet ID. Facet IDs are never recycled for other
   content; a revised page keeps its facet and changes its version.
2. **Proposed facet range 32–79**, subject to a check against the target
   shard's existing allocations. 0–31 stay with stock and shard facets; 127
   and 255 stay reserved. The standard map-change packet and ModernUO's map
   serialization carry one byte, so 32–79 needs no wider field.
3. **Bounded lazy residency.** A page's block index is built only when the
   player enters its facet. At most a small explicit cap of pages stays
   resident (default 3, maximum 5); the current page is never evicted, the
   least recently used one is. Cardinal neighbours are verified in the
   background but not indexed. Corner travel exchanges a page within the cap;
   it never grows it.
4. **Local verified packs first.** Pages come from a local manifest
   (`guo-page-manifest` format 1, data_formats §49) naming each page's MUL
   files and their SHA-256. A page is used only after its sizes, static index
   bounds and hashes match. Nothing is downloaded.
5. **Readiness before the crossing completes.** The client verifies and
   indexes the target page before it builds the new map. A page that cannot
   be made ready is refused out loud: the client disconnects with a message
   naming the doctor and builds no map, so it never plays on a facet the
   server did not choose. Only a server can refuse the move itself, which
   needs negotiation (deferred).
6. **Version invalidation.** A resident page built from content that no longer
   matches the manifest (new version, new hash, replaced or truncated file) is
   dropped and rebuilt on next entry. Every entry bumps a generation; a
   background result from an older generation is discarded.
7. **Deferred:** network content streaming, reusable facet slots, capability
   negotiation, server page registry and generalised crossings, UOP page
   archives, page-aware world map/minimap/markers.

## Implementation

L4 only: `src/Labs/Paging`, compiled in `GuoLabs=true` Debug builds and never
exported. Off unless `GUO_LABS_PAGING` names a manifest. With it set, the
first move to a page facet replaces the map loader with `PagedMapLoader`, a
MapLoader subclass widened to 80 facets the way upstream's UltimaLive loader
widens it; stock facets load exactly as before. One `PORT DEVIATION (GUO)`
seam in `World.MapIndex`, inside `#if GUO_LABS`. The manifest, verifier and
residency session are engine-free and tested with dotnet
(`tools/paging_tests`). Doctor: `launchers/dev/paging_doctor`. Before the
swap a preflight refuses paging when the stock loader carries applied map
patches, a custom `MapsLayouts` or the UltimaLive loader. The world map's
`ChangeMap` has the same kind of seam for page facets, and `World.Clear` one
that releases the pages with the world (logout, disconnect).

## ADR Dependencies

- ADR-0045 (wide zoom overview): `MapLoader.DataRevision` is bumped on every
  page load and eviction, so cached overview tiles are invalidated.
- ADR-0019 (Asset Store): `LoadMap` still applies Store map
  overlays to a page facet; packs have no blocks there unless they say so.
- L4 Labs policy in AGENTS.md (feature layers): build flag, doctor, scenario.

## Engine Compatibility

No Godot API is involved beyond the existing headless probe pattern (a `Node`
scene run by `godot-console`). Godot 4.7.2 mono, .NET 8. The paging core uses
only `System.Text.Json`, `System.Security.Cryptography` and file I/O.

## GDD Requirements Addressed

None: GUO is a port without GDDs. The requirement is the owner-approved paging
milestone 1 (Moshu via chief, mail 19516/19517/19528, 2026-10-28).

## Alternatives

| Approach | Verdict |
|---|---|
| 48 facets on the eager UltimaLive loader | Indexes every announced map; unbounded memory; diagnostic only |
| A few reusable facet slots, streamed logical pages | Remapping protocol, stale packet/cache hazards, reconnect translation; defer until stable IDs run out |
| One enormous facet | Legacy packet coordinate masks (X 15 bits, Y 14 bits); reject |

## Consequences

- Default builds and exports are unchanged: the deterministic default-build
  `GUO.dll` is byte-identical with and without the seams (run
  `20261028_guo-worker-paging_l4-milestone1`, `data/offstate_dll_parity.txt`,
  and for the world map seam `data/m2/attempt9/data/offstate_dll_parity_worldmap.txt`).
- While paging is active `MapLoader.MAPS_COUNT` is 80, so the world map's
  free-view menu lists 80 facets, as it lists 127 under UltimaLive.
- Paging and shard map patches are exclusive: with patches applied the
  preflight refuses paging (and disconnects) instead of dropping them.
- The world map loads an evicted page on demand within the cap and pins it
  while it renders. A pinned page is never evicted, replaced or rebuilt; a
  revision waits for the unpin, which also trims back to the cap at once.
  Residency is bounded by max(cap, pinned pages + the current page); a second `PORT DEVIATION (GUO)` seam in
  `WorldMapGump.ChangeMap`, inside `#if GUO_LABS`. A page it cannot load is
  named in the journal and the gump keeps its map.
- The shard side (map registration for 32–79, crossings, persistence,
  negotiation) is unchanged and needs its own decision.
