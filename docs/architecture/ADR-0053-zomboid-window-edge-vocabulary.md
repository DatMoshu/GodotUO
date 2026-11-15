# ADR-0053: Read both window spellings on Project Zomboid wall edges

Status: Accepted for local implementation by the acting project lead, 2026-11-15,
as a tool correction (`tools/layout_import`). No client, export or data-contract
change. ADR-0052 is reserved for the source-object furnishing proposal.

## Context

`tools/layout_import/zomboid.py` turns each source tile's properties into the
kind of edge it puts on its cell's north or west side: door, window or wall.
Project Zomboid spells the window property two ways:

- `WindowN` / `WindowW`: a wall piece with a window opening in it.
- `windowN` / `windowW`: the window object set in that opening.

The importer read only the lower-case pair. A wall piece that carried
`WindowN` or `WindowW` and no other edge property produced no edge at all, so
an opening with no window object in it left a gap in the wall instead of a
window.

A property census of one installed copy counted the upper-case spelling on
about 1,650 definitions per axis and the lower-case one on 138. About 1,610 of
the upper-case definitions per axis carry no other north or west edge property.

## Decision

One function, `edge_kind(props, axis)`, decides the edge for both the geometry
and the unmapped-detail count. It reads both spellings as a window. Order is
unchanged: a door property wins, then a window, then a wall.

Not changed, and recorded here so they are not mistaken for oversights:

- `solid` and `solidtrans` mark free-standing objects, not edges. They stay with
  the furnishing classifier (ADR-0052).
- `WallSE` is the south-east corner piece. It closes no north or west edge and
  stays unmapped.
- `HoppableN/W` and `TallHoppableN/W` (fences, low walls) are not read. Whether
  they become UO fences is a separate proposal.

## Measured effect

On 147 sampled buildings of one installed map, 137 resolve before and after
(the other 10 are refused as cross-cell, unchanged). 136 produce identical
cells. One gains one window where there was a gap: 879 windows become 880.
Doors (772) and walls are unchanged. In practice nearly every such wall piece
shares its cell edge with a plain wall piece or a window object, which is why
the gap was rare. A layout that had the gap gets a different semantic hash;
every other layout keeps its hash.

## Validation

- `test_zomboid_edge_kind_reads_both_window_spellings` covers both spellings,
  both axes, door precedence, plain walls and the three properties left alone,
  on synthetic property sets. No Project Zomboid data is committed.
- The `tools/layout_import` tests pass.
- The before/after census above was run against a local install; its output is
  kept with the change's evidence, outside the repository.
