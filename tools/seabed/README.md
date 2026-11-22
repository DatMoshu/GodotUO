# tools/seabed

Plans the seabed and shallows for a whole facet and stages the result outside
the UO install (ADR-0022). It is an L2 content generator: the client only
reads the staged statics through `--files-override`.

```
python tools/seabed/run.py generate [--facet 0] [--seed 1] [--out DIR]
python tools/seabed/run.py stats    [--facet 0]
launchers\world\seabed.bat generate
python tools/seabed/test_seabed.py
```

Default output: `<UO_BUILD_DIR>/seabed/map0/` with `staidx0.mul`, `statics0.mul`,
`files_override.txt`, `seabed.json` (the numbers). Play it with
`--files-override <out>/files_override.txt`. A shard needs nothing: statics on
the sea floor are client decoration, and its walk and boat rules are unchanged.
`write_stage` refuses a folder inside the install.

## The shallow rule

Felucca digs its shallows as land tiles 0x4C-0x64 at z -15 under water statics
0x1796-0x17B2 at z -5, stops them along an arbitrary line, and makes the rest
flat wet land (0xA8-0xAB, opaque). A cell is **shallow** when it is water within
`shallow_max` (14) cells (Chebyshev) of dry land: a dug bed, a bed row Felucca
raised to the surface (sunk to bed depth), or deep wet land, which is turned into
the same bed (land 0x64 at -15, plus a whole water static at -5). A chunk is a
**water chunk** when any of its 64 cells is wet, **eligible** when it holds a
shallow cell, **covered** when it received at least one plant, coral, rock or shell.

## Layout

- Floor: a seamless sand tile (0x9DE9-0x9DEC) on every shallow cell, hued by shore
  distance. The ramp (`HUE_RAMP`, `RAMP_PROGRESS`) is 16 hues that lie on the straight line, as the
  client shows it, from the shore colour to the open sea's own colour. Cells take the hue below or
  above their position on that line by probability (stochastic rounding), so the mean colour is exact
  at every distance and no band shows. The original opaque deep water measured (0, 54, 80) at Haven. No smooth hue
  lies on the line that close (the one that does, 1916, has a sparkling palette and shows as cyan speckled
  tiles), so the ramp overshoots to 1156, smooth and darker than the deep, and at the deep's distance the
  mixture of 1156 and 592 averages to the deep colour.
- Measuring (`calibrate.py`): `run.py generate --calibrate` stages 64 hues in an 8x8 pattern with
  no objects; a `--layer-shot` of Haven gives each hue's on-screen mean colour; `calibrate.py fit PNG`
  finds the cell lattice and fits observed = gain * predicted + offset per channel against the hued sand
  tile (gain 0.365, offset (0, 37, 55), residual 1.5 over 63 hues, correlation 0.99). `calibrate.py ramp`
  picks hues within 6 units of the line, one per 0.04 of progress, and drops palettes that speckle
  (1593, 1599, 1469, 1916) or tint (2571 purple) by their per-pixel spread. The table has no colour on
  the line between 0 and 0.17 and between 0.79 and 0.98; there plan() mixes the two neighbours.
- Objects (z floor+1, only where no other static stands): plant meadows, rock fields and
  coral heads from three value-noise fields (feature sizes 7, 9, 13 cells), density
  falling as `(1 - d/14)^1.5`, so nothing ends at an edge.
- Translucent water: `tiledata.mul` gets the Translucent flag on 0x1796-0x17B2 (the
  owner's Transwater convention), or the floor would be hidden. Translucent diamonds that
  overlap by a pixel blend twice and draw a seam, so `artLegacyMUL.uop` is copied with
  0x1797-0x179C trimmed so neighbours tile exactly once and the outline tile 0x1796 emptied.
  Trimming leaves the removed pixels open wherever the neighbour that would draw them has no
  water (a coast, the edge of a bed): the beach shows through as thin light diagonal lines. Four
  translucent "water edge" tiles (free static ids 0xFFFA-0xFFFD, appended to the stage art, flagged in the
  stage tiledata) hold exactly the pixels each neighbour takes, and plan() puts one on every whole water
  cell whose neighbour is missing. A land tile was not the cause: with the art untrimmed the lines vanish.
  All four edits are on stage copies; the install is never written.
- Same seed, same bytes.

## What reads the stage

Only the client, through `--files-override <out>/files_override.txt` (settings.json
`files_override` for a profile). It lists staidx, statics, the patched map UOP, tiledata
and art. A shard needs nothing: its walking and boat rules use its own data, and the added
statics and bed land sit below the surface. Without the override the client is untouched.

## Client art used (tiledata names; look at them with the editor's Statics panel)

| Role | Ids | Source |
|---|---|---|
| Floor | 0x9DE9-0x9DEC | "sand_tile": a seamless sand diamond, hued |
| Rocks | 0x1363-0x1365, 0x1368, 0x1369, 0x136B-0x136D, 0x1367, 0x136A | "rock", "rocks" |
| Boulders | 0x1771-0x1776 | "rock" (large brown and grey) |
| Plants | 0xD04 ("water plants"); 0xD05 "reeds" is left out (dark diamond base) | tiledata names |
| Corals and sponges | 0x4C2A-0x4C31 "Sea Coral"/"Sea Sponge"; 0xA386, 0xA38B, 0xA38D-0xA38F (anemone, fire, staghorn, plate, red coral) | tiledata names |
| Shells | 0xFC4-0xFCC | "shell", "shells" |

Felucca itself puts almost nothing under its water: 0x21A3 "nodraw" markers, pier
stumps, and a few trees on drowned islands (measured 2026-11-21 over map0).
References: the owner's Terra shard capture and Transwater pack (translucent
water statics over a seabed), UOFiddler for the ids, and the Dragon Mod 11
mapping tutorial for the land bed convention.
