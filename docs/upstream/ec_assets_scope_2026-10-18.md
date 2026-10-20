# Enhanced Client terrain and water in GUO: scope

Story UP-03, the scope part. No code. Written 2026-10-18 after the ClassicUO
2.0 beta started drawing Enhanced Client (EC) terrain and water inside the
classic client; see [drift_2026-10-18.md](drift_2026-10-18.md) for how that
beta was assessed.

## What ClassicUO 2.0 does

Read from the beta's released binary (strings only; the source is not
public, so none of it can be ported or copied):

- **Terrain.** A land tile of a terrain type is drawn from two EC textures of
  that type, blended by a noise mask, each tiled across the world in world
  coordinates (so neighbouring tiles join without a seam) and hued through
  the classic palette. Flat tiles skip the light as classic flat art does.
- **Water.** Water land tiles and wet statics take an EC water texture with
  a normal map that scrolls along a wind direction over time, clipped to the
  classic tile's shape. Its comments name the EC's own `LiquidTerrain.fx` as
  the model.
- **Data.** It reads the EC textures beside a classic install. It refuses an
  EC install on its own ("pick a classic client install").

## Why this is not a port

GUO transplants ClassicUO's C#. The 2.0 renderer is Rust on wgpu with WGSL
shaders, and closed. GUO would be writing its own feature to the same idea,
which puts it outside L0 (core port): ClassicUO 1.x does not ship it, and the
classic art stays GUO's default.

## Where it fits in GUO's layers

It is three pieces, and each has its own layer.

| Piece | Layer | Notes |
|---|---|---|
| Reading EC textures from the player's own EC install | Tools, then a local set | EC data is proprietary like the classic data (AGENTS.md rule 8). It can never be a Store pack that anyone downloads. It follows ADR-0034 instead: a tool mirrors the player's own files into a local, gitignored set that the client reads, and nothing derived from it is committed, published or bundled. |
| The textures and terrain mapping a renderer needs | L2, as a typed pack component | A pack kind (call it `terrain_textures`) with a typed schema: terrain type, texture pages, stretch per texture, mask. Shipped packs could carry **original** textures made for GUO (painted or generated, with provenance), and the local EC set answers the same lookups. One renderer, two sources. |
| Drawing land and water from them | L1 option, off by default | A new way of drawing land is a rendering-semantics change, so it needs an ADR and Moshu's decision (AGENTS.md, feature layers). The water lane's ADR-0037 (water render modes) is already deciding the water half; terrain should be a section of it or a sibling ADR, not a second mechanism. |

## What has to be found out first

1. **Which EC files hold the terrain and water textures, and how terrain
   types map to classic land ids.** No EC install is on this machine, so the
   file names are not verified here. The EC is a free download from the
   game's publisher; installing it for this research is Moshu's call
   (third-party software, several gigabytes).
2. **Whether the mapping from classic land ids to EC terrain types is in the
   EC data or has to be authored.** If it is authored, it is GUO content
   (a pack), not extracted data.
3. **Licence position for the local set.** The same as the classic data:
   the player's own install, read in place or mirrored locally, never
   redistributed. ADR-0034's wording can be reused.
4. **Parity proof for the off state.** With the option off, render dumps and
   the eight-place sweep must be unchanged; the option's scenario shows it
   on and off at one place.

## Proposed stories, in order

| Story | What | Layer | Needs |
|---|---|---|---|
| EC-0 | Identify the EC files and formats for terrain and water textures, and the terrain mapping; write `data_formats.md` sections before anything reads them | Tools | An EC install (Moshu's decision) |
| EC-1 | `tools/ec_extract`: mirror the player's EC terrain and water textures into a local set, with a doctor launcher that says when no EC install is configured | Tools | EC-0 |
| EC-2 | The `terrain_textures` pack kind and its schema, answered by Store packs and by the local set | L2 | EC-0, a Store lane review |
| EC-3 | Land drawing from terrain textures, off by default, behind the ADR shared with water | L1 | ADR-0037 accepted, the water lane's renderer |

EC-3 belongs to whichever lane owns ADR-0037's renderer (the water lane), so
the two features share one shader path and one Options entry design. The
upstream lane can do EC-0 and EC-1 once an EC install is available.

## Not in scope

- Porting any EC user interface. The brief rules it out, and the EC's UI is
  not what ClassicUO 2.0 uses either.
- EC animations, gumps or item art. The 2.0 beta does not use them, and GUO's
  HD packs (per-graphic replacement) already cover sharper art.
