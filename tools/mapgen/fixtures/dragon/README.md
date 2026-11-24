# Native Dragon map fixture

These scripts generate a 32 × 24 indexed BMP and a minimal, independently authored
set of Mod 11 scripts. They require Python 3 and a user-supplied Dragon 1.05.00
Beta prerelease #8a. The optional freezing check uses DragonSP 1.00.00 bundled
with that release. The experiment uses no client installation or UO artwork.

1. Choose a new destination directory for each run. From this fixture directory,
   run `python make_trial.py "path/to/Dragon.exe" trial --dragonsp "path/to/Dragonsp.exe"`.
2. Open the staged Dragon.exe **with trial as its working directory**. Select
   **Bitmap To Map**. Check that the input is trial.bmp and the output is map0.mul,
   then select **Start**. The INI supplies a size of four by three blocks.
3. Run `python check_trial.py trial/map0.mul`. It reads every cell using
   X-major block order, row-major in-block cells, little-endian tile ID and signed Z.
4. For the static pass open the staged Dragonsp.exe in the same working directory.
   Verify the two file fields point to the trial's own staidx0.mul and statics0.mul.
   Select **All**, then **Start**. Run `python check_trial.py trial/map0.mul --frozen`.
   The oracle compares all nine records to their expected (x, y, ID, Z, hue) tuples.

Repeat with these switches to isolate each variable.

| Input switch | Checker switch | Native #8a observation |
|---|---|---|
| Default | None | 386 tile 3, 360 tile 22 and 22 tile 57. The off-diagonal landmark is at (7,9), Z +19. |
| `--reverse` | `--reverse` | Duplicate masks in reversed order place tile 56 on all 22 edge cells. |
| `--recolor` | None | The palette RGB changes and the terrain payload digest stays identical. |
| `--superset-only` | `--superset-only` | The broader mask places zero edges. |
| `--edge-lift` | `--edge-lift` | A +3 adjustment lifts exactly 22 edge cells to Z +10. |
| `--random-tiles` | `--random-tiles` | The oracle accepts either valid base variant. Two fresh runs here yielded identical cell digests and counts, 188 tile 3 and 198 tile 4. |

The reference row-major cell SHA-256 is
`e5a66ed98c8b04dd5e1513baba9fb4e66490fe3b626ff9fe0fe6147a84ab2694`.
The checker omits the four-byte block headers from that digest. The original
Mod 11 archive SHA-256 is `a038ed84a2439a179fdfc782a313b1b82ec5c497413b11f2e5fd43650323b38b`.
Source attribution, binary versions, compatibility findings and later-tool
lineage are in the [Dragon report](../../../../docs/map-editor-comparison.html#dragon).

## Earlier compilers

Mod 10 prerelease #6 accepted the same small base fixture and produced the same
decoded payload as #8a. Dragon 1.03.62 Beta, shared by the sampled Imod9+ and Imod13
packages, rejected that input as the wrong BMP size. For 1.03.62, add `--classic`
to both the generator and checker. This creates separate groups.txt and maptrans.txt
inputs and extends the same shore across a 6144 × 4096 indexed BMP. Run this once
with the original rule order and again in a fresh folder with `--classic --reverse`.
Each output is 77,070,336 bytes. The checker verifies every block's tile IDs and Z.
The 4,094 interior shore cells use the **first** duplicate in 1.03.62. Prerelease
#8a uses the **last** duplicate on its 22 interior shore cells. These tests establish
precedence for identical masks and a single directed group pair.
