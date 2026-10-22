# The covering-land regression, and merged land + merged cover by default (PERF-REG)

**Owner's decision needed:** this note backs a proposal to draw the land array
(`--merged-land=array`) and merged cover (`--merged-cover`) by default on
Windows. Android already draws the land array by default (ADR-0017 amendment,
2026-09-28). Linux, the Steam Deck, macOS and the web are unchanged because they
have not been measured.

All numbers come from the perf probe on an optimised build: 2560x1440 at zoom 2.5,
Windows, the same shard and the same scenes.

## What regressed

Between 2026-09-27 (cb97933e) and main (96ce0ff2), every far-zoom scene got
44–65% slower:

| Scene | Sept 27 mean ms | main mean ms | change | Sept 27 draw calls | main draw calls |
|---|---:|---:|---:|---:|---:|
| open-field | 5.6 | 8.1 | +44% | 1,784 | 2,496 |
| britain-bank | 24.9 | 39.8 | +60% | 8,148 | 11,929 |
| dense-forest | 11.7 | 19.1 | +63% | 3,593 | 5,449 |
| dungeon | 14.5 | 23.9 | +65% | 6,705 | 9,308 |

The cause is the riverbank cover fix (8b819bb8). It queues covering land
(`RenderLists.CoverFromBelow`) for more sunk objects, so it keeps water drawn
correctly beside land that stands above it. At the bank the number of covering
tiles grew from 3,323 to 6,847. Each covering tile is a separate canvas draw.
With covering turned off, the bank runs at 19.6 ms and the dungeon at 5.4 ms.

## The trigger stays as it is

A tighter trigger was tried and rejected: it skipped tiles whose highest corner
is at or below the object's z. It changed 436–958 repeatable pixels per scene.
The covering tiles that one static queues also hide nearby mobiles' feet, and
that matches what ClassicUO's depth buffer does. Narrowing the rule loses those
tiles, so the fix's trigger is left alone.

## Draw the covers cheaper instead

Merged cover (Epic B, B4 fix 2c) draws each run of consecutive covering tiles
as one mesh over the land array's Texture2DArray. It draws the same tiles in the
same order.

| Scene | main mean ms | fps | merged mean ms | fps | main draw calls | merged draw calls |
|---|---:|---:|---:|---:|---:|---:|
| open-field | 7.9 | 126.8 | 7.0 | 142.4 | 2,539 | 749 |
| britain-bank | 38.5 | 26.0 | 25.1 | 39.9 | 12,322 | 2,762 |
| dungeon | 22.9 | 43.8 | 10.3 | 97.0 | 9,088 | 1,701 |

p99 at the bank is 49.5 → 34.5 ms, and in the dungeon 31.6 → 14.7 ms.

## Parity

The probe's `--perf-parity` compares five frames with the switch on and with
it off. A "strict" pixel is one that differs in every frame.

| Comparison | Scene | strict px | where |
|---|---|---:|---|
| merged cover on/off (both over the array) | britain-bank | 0 | |
| | dungeon | 0 | |
| land array vs per-chunk land | britain-bank | 30 | a walking NPC's overhead name |
| | dungeon | 5 | the ettin's overhead name |

The land around both labels is identical. The 35 pixels are overhead text drawn
over a moving mobile, so they come from motion between the two captures and
are not in the land.

## The river scenes

The riverbank fix exists for three visual_ab scenes: river-healers,
river-north and river-west. They are drawn with ClassicUO, GUO plain
(`--merged-land=off --merged-cover=off`) and GUO with the new defaults, at
1600x900:

| Scene | new defaults vs plain GUO | what differs |
|---|---:|---|
| river-west | 0 px | |
| river-north | 20 px | five mobile dots on the radar gump |
| river-healers | 554 px | an animal that walked off between the shots (518), radar dots (36) |

The world, water edges included, is the same pixel for pixel. The riverbank
fix still covers what it covers; it is only drawn in runs.

### Correction, 2026-10-22 (PERF-V7): river-west is not 0 px

The table above is the 2026-10-19 result as it was measured then and is
kept as such. The main validation of 2026-10-22 (Codex perf seat, run
`20261022_guo-codex-perf-20261022_main-live`, `river-exact-diff.json`)
compared one sequential plain / merged-cover pair at 1600x900 exactly in
RGB, with the name labels and chat excluded by rectangle and no radar in a
fresh profile, and found three world pixels:

| pixel | plain | merged |
|---|---|---|
| (802, 16) | 35 2d 1e | 35 26 1e |
| (575, 233) | 33 2b 19 | 44 3b 2b |
| (1334, 264) | 3d 42 0f | 3d 48 1a |

What that evidence establishes: three world pixels differ between the two
paths in that pair, each isolated (the 3x3 ring around each is identical
in both stills). A neighbourhood check of the same two stills (run
`20261022_guo-fable-perftour-20261022_live-plan`,
`guo_PERF-V7_river-pixel-neighbourhood_data_20261022.json`) finds the other
path's colour within 2 px of the pixel, in the same still, for (802, 16)
and (575, 233), and not for (1334, 264). That the pixels sit on stretched
riverbank tiles is read from their position on the river in the stills, not
from the tile data; it is a reading, not a source-linked fact. None of this
establishes the cause.

Hypothesis, not yet reproduced under control: plain GUO samples each
tile's own texture, while the merged cover path draws a run as one mesh
over the land `Texture2DArray` with the tile's UVs scaled by `w / Size`
into the page and back by `Size` in the shader; on a stretched tile, whose
corners are pulled to its z, that rescaling may round a nearest-sampled
UV at a texel edge to the other side for a few fragments. A controlled
reproduction (the same tile drawn both ways from a fixed camera, the
fragment's UVs dumped) is the next step; until then neither the cause nor
whether the paths can be made exactly equal is settled, and no pixel
difference is waived. `tools/visual_ab` now counts pixels exactly in any
channel and reports isolated pixels beside the count for information.

## The switches

- `--merged-land=off` and `--merged-cover=off` restore upstream's per-tile draw
  on Windows.
- visual_ab's plain `guo` variant now passes both, so it still means "plain".
- The land array costs about 80 MiB of GPU memory (ADR-0017 amendment).

## Next

The depth-buffer path (PERF-GPU, ADR-0037) removes covering land altogether.
This default is the cheap fix until that lands.

## Validated on main 2b2999d6 (2026-10-22): performance measurements

The performance improvement is present on main. **Full validation is incomplete:**
the stock count gate cannot record its baseline, a six-scene diagnostic comparison
fails, and the river pair does not reproduce the expected zero world-pixel diff.
These results validate the timing benefit, not every acceptance check.

Source inspection at `2b2999d699fbcbd1797aabe7f0b03ebed1d9eb51` confirms:

- `src/Bootstrap/Main.cs:1920-1923` enables the land array and merged cover on
  Windows before parsing command-line arguments. Its helper at lines 1872-1876
  sets the enabled, ordered and array flags together.
- `Main.cs:1992-1994` and `2051-2053` implement the two `off` overrides.
- `src/Game/Scenes/RenderLists.cs:501-510` uses ordinary chunk land meshes when
  merged land is disabled; `src/Render/UltimaBatcher2D.cs:820` guards cover merging.
  Thus plain GUO already uses chunk land meshes; the switches disable the added
  merging paths.

### Three runs per mode

Windows, Godot 4.7.2 .NET, Forward+, RTX 4090, Ryzen 7 7800X3D, optimized build,
2560x1440, zoom 2.5, Classic PostFX (zero passes), 360 measured frames per scene
after settling. One exclusive benchmark lease covered all measurements; the
initial Godot process inventory was empty. Each run used a separate probe home.
The four scenes were selected with the existing `--perf-scene` flags. Plain
also passed `--merged-land=off --merged-cover=off`.

Current values below are the median of three per-run means and the median of
three per-run mean draw counts. Historical values are the branch measurements
above, not a controlled comparison across dates. The shard has moving entities;
the raw reports retain each run's counts and timings.

| Scene | Oct 19 plain ms / calls | Oct 19 merged ms / calls | Main plain ms / calls | Main defaults ms / calls | Frame-time reduction | FPS plain to defaults |
|---|---:|---:|---:|---:|---:|---:|
| open-field | 7.9 / 2,539 | 7.0 / 749 | 10.623 / 2,554.3 | 8.581 / 756.6 | 19.2% | 94.1 to 116.5 |
| britain-bank | 38.5 / 12,322 | 25.1 / 2,762 | 44.366 / 12,308.7 | 27.287 / 2,761.5 | 38.5% | 22.5 to 36.6 |
| dense-forest | 19.1 / 5,449* | Not reported | 22.474 / 5,467.6 | 13.541 / 1,440.5 | 39.7% | 44.5 to 73.8 |
| dungeon | 22.9 / 9,088 | 10.3 / 1,701 | 26.238 / 9,105.5 | 12.748 / 1,744.1 | 51.4% | 38.1 to 78.4 |

\* The forest's historical plain value comes from the earlier regression table;
the paired table did not publish a merged forest result.

The relative FPS improvement holds: bank gains 14.1 FPS (62.6%) and dungeon
40.3 FPS (105.8%) over today's plain path. The old absolute merged figures of
39.9 and 97.0 FPS were not reproduced: today's medians are 36.6 and 78.4 FPS.

At the bank, median land-mesh time falls from 1.611 to 0.118 ms/frame and
cover-land time from 5.565 to 1.119 ms/frame. Median gather time is 8.784 to
7.249 ms, sort 2.520 to 2.163 ms, and statics 6.422 to 9.567 ms. Full PERF-03
stage tables are retained with the evidence; independently calculated stage
medians need not sum to the median total frame time.

### Gate and river results

| Check | Result |
|---|---|
| `perf_gate --doctor` | PASS with explicit local engine, upstream and external build paths |
| Stock `--fixture dev-world-v1 --record-baseline`, 1280x720 | Exit 2: successful probe report rejected with `report contains an unknown scene`; no baseline saved |
| Stock compare | Exit 2: no baseline for this machine/name |
| Diagnostic `core-six` baseline | Recorded using the existing scene filters for open-field, britain-bank, dense-forest, dungeon and walk (login is automatic) |
| Diagnostic `core-six` compare | FAIL, exit 1: dungeon batcher items 158 to 160; other five scenes PASS |
| River-west fresh-profile pair, 1600x900, zoom 0.6 | 17,704 exact RGB-different pixels; three remain outside the documented name/chat text rectangles |

The stock probe now includes water/community scenes, while `tools/perf_probe/gate.py`
requires exactly its original six scenes. The diagnostic uses a separate baseline
name and explicit filters, at the gate's default zoom 1 and Forward+ renderer;
it does not turn the stock invocation into a pass. Draw-call counts passed in
all six diagnostic scenes. The dungeon failure is the zero-tolerance batcher
item check, measured against a freshly recorded baseline on the same main build.
No thresholds were relaxed and no baseline was committed.

The first river pair was obscured by a saved world-map overlay and was retained
as unsuitable evidence. The fresh-profile pair exposes the river and has no radar.
After excluding the visible name and chat text rectangles, world differences
remain at `(802,16)`, `(575,233)` and `(1334,264)`. Their cause is unclassified.
This is one sequential pair, **not** the probe's five-frame temporal-persistence
test; it does not establish a zero strict-pixel result. Screenshots, exact RGB
values and exclusion rectangles are retained for review.

Local evidence run: `20261022_guo-codex-perf-20261022_main-live`. Six accepted
timing runs completed from 15:42:23Z to 15:50:42Z; the final river client exited
at 16:00:05Z. The exclusive lease was released after confirming no Godot processes
remained. A preliminary timing run with sandbox shader-cache errors was excluded.
The 99 synthetic perf-tool tests pass; they are not live-gate evidence.

The CHANGELOG's proposal wording remains unchanged pending Moshu's confirmation.
Gate repair and investigation of the remaining pixels are follow-ups; this
validation changes no renderer code.
