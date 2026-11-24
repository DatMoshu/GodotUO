# ADR-0054: Detect shard map data layouts from the files themselves

Status: Proposed by uo-maps-lead for GUO tasks 2393 and 2394, 2026-11-21, at the
owner's request ("update GUO to support these maps if detected"). Two client
port deviations, each active only when a file contradicts what the client
version implies. Official data loads exactly as before.

## Context

Community shards ship their own client data on top of a modern install: a
German roleplay shard's map (Terra Nova, Vespertales), a Brazilian shard's
continent (Epic!), a Siege-era Britannia (Angel Island). UO-Maps tested them
in GUO on an isolated shard (the UO-Maps test results of 2026-11-21).
Two upstream ClassicUO assumptions break them:

1. **tiledata.mul layout.** `TileDataLoader` reads 64-bit flags for any client
   at or above 7.0.9.0. Shards built for older clients ship the 32-bit layout
   (Vespertales and Angel Island: 1,036,288 B; Terra Nova: 3,187,756 B, the old
   layout extended to 2,323 item groups). Read as 64-bit, names come out as
   "UNUSED", floors and roofs vanish, and walkability is wrong, so walk-to
   stops after a few tiles. ModernUO already decides by file size.
2. **Map size.** Facet sizes come from the client version, with one upstream
   exception (a map0 MUL of exactly 393,216 blocks is 6144 wide). A 6144x4096
   map in UOP form, or in another facet's slot (Epic!'s map2), is indexed as
   the default size and reads past its last chunk. Until now the only way
   round it was a hand-written `maps_layouts` setting.

## Decision

Both checks live beside the loader they change and are marked
`PORT DEVIATION (GUO): ADR-0054`.

- `TileDataLayout.IsOld(length, versionSaysOld)`. Each layout has a fixed land
  block (512 groups of 32 entries, 4-byte group header; entries of 26 or 30 B)
  followed by whole static groups (4 + 32 x 37 B or 4 + 32 x 41 B). When the
  file length fits exactly one layout and that layout disagrees with the
  version, the length wins and a warning is logged. Otherwise the version
  decides, as upstream. Every official tiledata fits only the layout its
  version implies, so nothing changes for it.
- `MapSizeInference.Apply` runs only when `maps_layouts` is unset. A facet's
  terrain block count (MUL length / 196, or the summed decompressed UOP
  chunks / 196) that equals its default size changes nothing. A count that
  contradicts the default takes the official size it matches exactly, else
  the default height with the width the count implies (at most 7168). A
  warning names the facet and the size chosen. `maps_layouts`, when set,
  still overrides everything.

No new setting and no data-contract change.

## Consequences

- Shard data dropped into a custom data folder renders and walks without
  `maps_layouts` or converted files. Verified: Terra Nova on its raw data
  walks to target with this change and stops 7 tiles short without it; the
  official Britannia control is unchanged.
- A file that fits both tiledata layouts (no known case) or neither still
  follows the version, as upstream.
- Server side is out of scope here: a ModernUO shard hosting the same maps
  also needs a size-correct `Data/map-definitions.json`, and shard-made UOP
  maps need their chunks ordered by name (`tools/modernuo/patches`).

## Negative cases

`godot/GUO/src/Assets/MapInferenceProbe.tscn` (in `launchers\dev\smoke.bat`) asserts,
on synthetic lengths and counts, that nothing changes for official data:

- Official old (1,036,288 B) and new (3,188,736 B) tiledata keep the version's
  answer, as do lengths that fit neither layout and truncated or padded files.
- A length can fit both layouts. 428,032 + 1,188a = 493,568 + 1,316b has
  solutions; the smallest is 601,480 B (146 old groups = 82 new). The version
  decides there, as upstream. No known file has such a length.
- Default-size block counts, and zero, negative or non-multiple-of-height
  counts, are rejected; a count wider than 7168 is rejected; a UOP with no
  entries reports no count.
- Found and fixed: 2560x2048 and 1280x4096 have the same block count (81,920),
  so 1280x4096 could never be inferred. A match that keeps the default height
  now wins; otherwise the first listed size still wins as before.
- Not changed: a count that is a multiple of the default height is accepted at
  any width up to 7168, so a very small file infers a very narrow map.
