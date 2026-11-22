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

Felucca already digs its shallows: land tiles 0x4C-0x64 at z -15 under water
statics 0x1796-0x17B2 at z -5. Deep water is flat wet land (0xA8-0xAB) with no
floor to decorate. A cell is **shallow** when it is such a dug bed (water static
at least 5 above its land), no further than 12 cells (Chebyshev) from dry land,
and holds no other static. A chunk is **eligible** when it has a dug-bed cell
within that band, **covered** when the plan placed at least one object in it,
and a **water chunk** when any of its 64 cells is wet land or a wet static.

## Layout

Shore distance `d` splits the shallows into a rim (d <= 2: rocks, boulders, sand,
sparse reeds), a shelf (3..6: plant meadows, sand, coral from 4) and a slope
(7+: boulder fields, coral heads, thin weed). Three value-noise fields (feature
sizes 7, 9, 11 cells) make rock fields, plant meadows and sand patches, so
objects clump and leave clear floor between. One object per cell, on the land
z, hue 0; same seed, same bytes.

## Client art used (tiledata names; look at them with the editor's Statics panel)

| Role | Ids | Source |
|---|---|---|
| Sand patches | 0x2468-0x246B, 0x245E-0x2463 | "sand" ripple diamonds in the install's tiledata/art |
| Rocks | 0x1363-0x1365, 0x1368, 0x1369, 0x136B-0x136D, 0x1367, 0x136A | "rock", "rocks" |
| Boulders | 0x1771-0x1776 | "rock" (large brown and grey) |
| Plants | 0xD04 ("water plants"), 0xD05 ("reeds") | tiledata names |
| Corals and sponges | 0x4C2A-0x4C31 "Sea Coral"/"Sea Sponge"; 0xA386, 0xA38B, 0xA38D-0xA38F (anemone, fire, staghorn, plate, red coral) | tiledata names |
| Shells | 0xFC4-0xFCC | "shell", "shells" |

Felucca itself puts almost nothing under its water: 0x21A3 "nodraw" markers, pier
stumps, and a few trees on drowned islands (measured 2026-11-21 over map0).
References: the owner's Terra shard capture and Transwater pack (translucent
water statics over a seabed), UOFiddler for the ids, and the Dragon Mod 11
mapping tutorial for the land bed convention.
