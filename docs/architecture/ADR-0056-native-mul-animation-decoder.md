# ADR-0056: Native MUL animation frame decoder

## Status

Proposed 2026-11-24 by the GUO lead, for the owner's decision. Recommendation: **do not adopt**; keep the
ported managed decoder. The candidate code stays archived on `perf/approved-client-hotpaths` in the
2026-11-23 cleanup bundle.

## Problem and scope

`perf/approved-client-hotpaths` added an optional Windows x64 sidecar, `guo_mul.dll` (Rust, about 30 lines),
that decodes one run-length MUL animation frame. `AnimationsLoader.ReadSpriteData` (a ported file) calls it on
the MUL path when the DLL is present and falls back to the original loop when it is not or when it refuses a
frame. The UOP path (`alphaCheck` true) is unchanged. The owner asked for a measurement before any decision:
cold animation load cost and a pixel parity test.

## Measurement (2026-11-24)

A standalone harness ran over every animation block of `anim.mul` to `anim6.mul` in a stock Classic install:
106,444 blocks, 610,418 frames, 1.01 billion pixels, already in memory (file IO is not measured). The managed
loop is the ported one, copied as it is. Release .NET 8, median of 7 rounds after warm-up, on the development PC.

| Decoder | All frames | Per frame |
|---|---:|---:|
| Ported managed loop (today) | 1.82-1.92 s | 2.98-3.15 us |
| Managed, palette expanded once to 256 x 32-bit | 1.18 s | 1.94 us |
| Native `guo_mul_frame_v1` | 0.54-0.58 s | 0.88-0.96 us |

Parity: the native output equals the managed output on all 610,418 frames, with no refusals.

## What that means for a player

Frames are decoded on demand, per animation group. A body action of 5 directions x about 10 frames saves
about 0.1 ms the first time it is shown; a screen filling with a few hundred new frames saves well under a
millisecond, once. The cost after that is the texture upload and drawing, which this does not touch. Compared
with the frame budget (16.7 ms at 60 Hz) the gain is real but rarely visible.

## Decision (recommended)

Do not adopt. The cost is permanent and the gain is small:

- It edits a ported file (rule 2), which must be reconciled on every upstream merge.
- It ships a native binary built by an optional Rust toolchain with the MSVC linker, a second build path for
  every Windows export, absent on Android, web and the Steam Deck, so those platforms never see the gain.
- The managed precomputed-palette variant gets a third of the gain with no native code, but it is still an
  opportunistic change to a ported file.

Revisit if a measured hitch on a real route (for example a crowded bank on first arrival) is attributed to
`ReadSpriteData`. Then propose the managed palette change first, as a marked `PORT DEVIATION (GUO)` with a
parity test, before any native code.

## Alternatives

- Adopt the native sidecar as written (Windows x64 only), with the managed fallback.
- Adopt only the managed palette expansion (portable, about 35% of the decode cost).
- Offer either upstream to ClassicUO, so GUO receives it by merge rather than by deviation.

Harness and numbers: the GUO run folder `20261124-muldec` in the shared runs area.
