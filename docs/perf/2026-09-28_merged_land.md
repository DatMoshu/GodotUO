# Merged land (Epic B, B4 fix 1): quiet rerun

2026-09-28, RTX 4090, 2560x1440, zoom 2.5 (the camera's maximum), vsync,
the frame cap and upstream's draw pacing lifted, 360 frames per scene.
Order: plain, `--merged-land`, `--merged-land=ordered`, plain again. The
second plain run is slower because free memory fell from 18 to 13.6 GB when
another job started, so the first plain run is the reference. Idle during
all runs: another session's touch-probe client (about 2% CPU), an MGS5 lab
(1.4%) and the shared shard (0%).

| Scene | plain mean ms | by texture mean ms | ordered mean ms | plain draw calls | by texture | ordered | parity by texture (px) | parity ordered (px) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| open field | 24.8 | 20.6 | 23.4 | 1,743 | 539 | 1,340 | 268 | 0 |
| Britain bank | 62.1 | 57.4 | 63.4 | 8,025 | 4,047 | 7,698 | 1,115 | 0 |
| dense forest | 38.3 | 37.7 | 39.3 | 3,054 | 1,457 | 3,089 | 1,130 | 0 |
| dungeon | 34.4 | 29.7 | 36.0 | 6,484 | 4,192 | 5,902 | 469 | 52 |

**Reading it:**

- **By texture** (one mesh per texture): land meshes drop to 2-3 a frame,
  draw calls by 50-69%, and mean frame time by 1-17%. It breaks pixel parity
  along land tile edges, where neighbouring diamonds overlap and the new
  order puts a different tile on top. The maximum channel difference is 86.
- **Ordered** (only consecutive same-texture runs merged, the original order
  kept): parity is exact in three scenes (the dungeon's 52 pixels are
  unexplained; flickering torches are plausible). But consecutive runs rarely
  share a texture, so draw calls fall only 4-23% and frame time does not
  move.
- **Neither is a go as built.** Both the win and parity need all land in one
  mesh in its original order. That means one texture for all land: land
  atlas pages as layers of a `Texture2DArray` in the land mesh shader only
  (ADR-0007's step 1, scoped to land). Expected: land meshes at 2-3 like the
  by-texture merge, with exact parity.
- **Covering land** (the tiles drawn again in the sorted pass) is untouched
  by either: 345-3,557 draw calls a frame. That is fix 2.

At 1280x720 and zoom 0.7, the ordered mode is exact in all five scenes, and
frame time is within noise of plain (1.5-2.2 ms).
