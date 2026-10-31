# Minimap on its own texture (atlas split A)

Split from the bundled atlas change (#157) at the owner's request: this is
one of its three parts, on its own toggle. Off by default.

## Problem

`MiniMapGump` paints the map around the player into the pixels of its own
gump art (5010/5011), which live in a region of the shared gump atlas
(`TextureAtlas.TryWriteRegion`). Godot cannot update part of a texture, so
every repaint marks that whole gump page dirty and the next flush re-sends it
in full (16 MiB for a 2048 page) while the minimap is open and the player
moves.

## Change (`GUO_MINIMAP_TEXTURE=1`)

Layer: L0 seam, `PORT DEVIATION (GUO)`, behind an environment toggle; no
profile field or Options entry until the owner decides.

The map is painted into a small `ImageTexture` of its own, the size of the
gump (a few hundred pixels square), and drawn from it at the same place,
size, hue and depth with nearest sampling. The gump atlas page is no longer
touched. Off, nothing changes.

## Timing difference (a parity difference to decide on)

Today a repaint dirties the atlas page and the page uploads at the start of
the next frame, so the minimap shows a step **one frame after** it is
painted. With the toggle on, the small texture updates as it is painted and
the minimap shows the step **in the same frame**. Every other pixel is
unchanged. That is a timing change in what the player sees, not an
identical frame, which is why it is off by default.

## Measurement

Pending a runtime window: frozen-world parity on the six gate scenes with
the minimap open, off/on beside an off/off control pair; the pinned 60 s
bank-to-docks walk with the minimap open, off/on with repeated interleaved
controls, p50/p95/p99, worst frames, atlas upload bytes, count and duration,
page count, texture switches and draw calls. Results will be added here,
including any slower outcome and raw pixel differences.
