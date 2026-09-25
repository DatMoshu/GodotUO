# Overnight sprints

Written 2026-09-23 from the deep review of `main` (build clean, smoke OK,
upstream at the reviewed pin, audit 396/396). Each sprint is one branch, one
commit, one acceptance check, and is independent of the others unless it says
so. Run them in order; a sprint that cannot meet its check is left on its
branch with a note, not merged.

## Standing rules for the session

- Work on `main`. Branch `work/S<n>-<slug>` per sprint. Commit on the branch;
  never commit to `main` directly. `spike/2d-lighting` is not touched.
- Every commit: `dotnet build godot\GUO\GUO.csproj -t:Rebuild` is 0 errors and
  `launchers\dev\smoke.bat` says OK. A sprint that touches the renderer or
  input also needs the probe named in its check.
- The shard must be running for playtest-style checks (`launchers\shard\run.bat`).
  Scripted runs use `--play`. Only one client on the `test` account at a time:
  two logins on it drop each other.
- Rules 1–8 in `CLAUDE.md` apply. In particular: port faithfully, mark every
  deviation `PORT DEVIATION`, never edit `sources/`.
- End each sprint by appending a dated entry to `## Log` at the bottom of this
  file: what changed, what the check showed, what was left.

## S1 — Plugin host: packets that grow

**Why:** `tools/plugin_host/src/Program.cs` rents a buffer sized to the
original packet and then lets the managed plugin change `length` in
`ProcessRecvPacket` / `ProcessSendPacket` (`tools/plugin_host/src/Plugin.cs`
around 371–417). A Razor-style plugin that grows a packet copies past the
rented array and then past the native buffer GUO handed over. Upstream ran
assistants out of process, so this glue has no upstream equivalent. Highest
risk item for the Discord testers, who will run Razor.

**Do:** size the rented buffer to the maximum a plugin may return (upstream's
bootstrap uses a fixed large buffer; match it), validate the grown length
against the destination capacity on the GUO side of `PacketInPlugin` /
`PacketOutPlugin`, and fail the packet with a `Log.Warn` rather than write past
the end. Add a case to the managed test plugin in `tools/plugin_probe` that
grows a packet on receive and on send.

**Check:** `launchers\dev\plugin_probe.bat` passes with the new case;
`launchers\dev\razor.bat` (on the spike branch only — copy the launcher if
needed, do not merge the branch) loads Razor and a session survives five
minutes of walking with Razor's packet filters on.

## S2 — Selection highlight left in the chunk mesh

**Why:** `GameScene.cs` ~966–1001 bakes the highlight hue into the shared
chunk mesh's vertex data (`ApplyMeshHue`) instead of upstream's redraw-on-top.
The restore is skipped when `_prevMeshHighlight.IsDestroyed`, so a highlighted
static that is picked up or chopped leaves its highlight in the mesh until an
unrelated rebuild. The deviation also has no `PORT DEVIATION` marker.

**Do:** restore the previous hue before the destroyed check (the vertex index
is still valid until the chunk rebuilds), or mark the chunk mesh dirty on the
destroyed path. Add the `PORT DEVIATION` comment naming ADR-0004. Do not
change the highlight approach itself.

**Check:** `launchers\dev\mesh_probe.bat` passes. Scripted `--play` run:
hover a static, `[go` away and back so the chunk reloads, screenshot shows no
stray highlight. Attach the screenshot path in the log entry.

## S3 — Formalise the SDL substitution and fix the audit's tiers

**Why:** 28 files have `using SDL3;` rewritten to `using GUO.Platform.Sdl;`
with no rule describing it. `LoginScene.cs` and `GameCursor.cs` call Godot
APIs but the audit tiers them `shim` because their upstream imports only XNA
math. `MapGump.cs:75` has an unmarked `Width`→`GetWidth()` change.
`CLAUDE.md` quotes stale tier counts.

**Do:**
1. `docs/port_plan.md` §2 and §3: add the SDL3 → `GUO.Platform.Sdl`
   substitution as a named shim-tier transform, with what
   `src/Platform/Sdl` provides.
2. `tools/port_audit`: a file whose port contains `Godot.` calls outside a
   `ToGodot`/`FromGodot` conversion is `rewrite` regardless of upstream
   imports. Re-run `launchers\pipeline\03_port_audit.bat`.
3. Mark the `MapGump.cs`, `LoginScene.cs`, `GameCursor.cs` deviations.
4. Update the tier table in `CLAUDE.md` from the regenerated
   `docs/port_status.md`.
5. `.gitignore`: add `.claude/worktrees/` and `tools/godot/templates/`.

**Check:** audit runs, `port_status.md` regenerated, `git status` clean of the
two folders, build + smoke.

## S4 — Renderer and input small fixes

All low risk, each its own commit on one branch.

- `UltimaBatcher2D.DrawStretchedLand` throws; `LandView.Draw` still calls it.
  Unreachable today because meshes build before the sort, but a land tile
  changed without dirtying its chunk would crash the frame. Guard in
  `LandView.Draw`: if `!InChunkMesh`, fall through to the flat `DrawStatic`
  branch, with a `PORT DEVIATION` note.
- `GodotInput.cs` ~173–201: `WheelLeft`/`WheelRight` fall through to a
  warning twice per notch. Ignore them explicitly (upstream reads only
  `wheel.y`).
- `GameController.cs:51`: dead `_ignoreNextTextInput`. Remove; the live one is
  in `GodotInput.cs`.
- Unused `using Godot;` in `Game/Weather.cs`, `IO/Audio/UOSound.cs`,
  `IO/Audio/AudioHost.cs`. Remove.
- `GameScene.cs` ~1023/1054: `SetStencil` is a no-op. Add a one-line comment
  pointing at ADR-0002 above the upstream comment; keep the call.
- `Client.cs:216–232`: `Client.Game` static is replaced silently in Release.
  Make the guard a runtime throw.

**Check:** build, smoke, `launchers\dev\render_probe.bat`,
`launchers\dev\batcher_probe.bat` (130 checks), one scripted `--play` run
with a screenshot.

## S5 — Second review pass over what was not read

The first pass did not read `Render/Camera.cs`, `Render/MeshLayer.cs`,
`Fonts.cs` / `FontGlyphAtlas.cs` / `SpriteFont.cs`, the bodies of
`uo_hue*.gdshader`, or `PluginClrHost.cs`'s callback path; sampled 2 of 34
launchers; and did not compile the Python tools.

**Do:** review those against upstream the way the first pass did (parity
deviations, leaks, per-frame allocations, unmarked edits). `python -m
py_compile` every `tools/**/*.py`. Check every launcher calls
`_shared\common.bat` first and hardcodes no path. Write findings to
`docs/review_2026-09-23_pass2.md`, ranked, CONFIRMED vs SUSPECTED. Fix
nothing in this sprint.

**Check:** the document exists and every finding has file:line.

## S6 — Parity sweep with the A/B tool

Phase 7 is measured by playing. `launchers\dev\ab_compare.bat` stands
ClassicUO and GUO in the same places on the shard and stacks the frames.

**Do:** run it over at least eight places covering: a town street at noon,
the same at night, a forest, a dungeon entrance, water and shoreline, a
multi-storey building with the player inside, a shop gump open, the paperdoll
and backpack open. For each `ab.png`, note visible differences in
`docs/parity_2026-09-23.md`: where, what, and the suspected upstream code path.
Fix nothing beyond one-line obvious cases; list the rest for the morning.

**Check:** the document lists every place with its `ab.png` path. Needs the
shard and the ClassicUO reference build the tool expects; if either is
missing, say so in the log and stop the sprint.

## S7 — Blend-state coalescing (measure first)

`UltimaBatcher2D.SetBlendState` (~410–431) creates a back-buffer copier and a
new item on every call, even when the previous draw used the same blend.
ADR-0003 budgets "a handful of effects". Nobody has measured a spell-heavy
scene.

**Do:** first measure. Scripted run with twenty or more effects on screen
(`[cast` or a shard command that spawns effects; the dev shard's `[` commands
are documented in `tools/modernuo/README.md`). Record frame time. Only if the
frame cost is visible (over 1 ms), coalesce consecutive identical blend states
the way `EnsureMaterial` no-ops, and measure again.

**Check:** before and after numbers in the log entry; `blend_probe.bat`
passes; `endurance.bat` five minutes holds frame time.

## S8 — Tester build from main

**Do:** re-export the Discord tester zip from `main` (the current zip was
exported from the spike checkout and carries live F9/F10). Export needs
`godot/GUO/GUO.sln` (gitignored; create with `dotnet new sln -n GUO --format
sln` and `dotnet sln add`) and the mono templates in
`%APPDATA%\Godot\export_templates\4.7.2.stable.mono`. Output
`build/release/GUO-test-<date>-<sha>.zip` with `config.bat` and `README.txt`
as before. Confirm `data_GUO_windows_x86_64/plugin_host/` is in it.

**Check:** the zip unpacks and `GUO.bat` reaches the login screen against the
local shard with a fresh `UO_CACHE_DIR`.

## Night 2 — planned 2026-09-23 evening

Inputs: `docs/parity_2026-09-23.md` (F1–F4), `docs/review_2026-09-23_pass2.md`
(findings 1–15 and the launcher table), the S8 log's "not proven" note, and
the uncommitted multi-client work in the tree. Same standing rules as night 1.
`launchers\dev\multi_client.bat` is the new end-of-sprint check for anything
that touches the world draw or input: it runs the playtest, the effects probe,
the highlight probe and a Britain photograph at once, on four accounts.
Order matters for S9 only; the rest are independent.

## S9 — Land the multi-client runner

**Why:** eleven changed files and two new folders are sitting uncommitted:
`tools/multi_client/`, `launchers/dev/multi_client.bat`, `--account`,
`--password`, `--character`, `--window-position`, `--window-size` and
`--sound` in `Bootstrap/Main.cs`, pinned window state in `GameController` and
`GameScene`, muted audio for every scripted run, the highlight probe centred
on the world view, and the shard patch that makes `UO_SHARD_GM_ACCOUNTS`.
Run 5 was three lanes OK and the session lane 31/32.

**Do:** `-t:Rebuild`, `smoke.bat`, `03_port_audit.bat` (GameScene changed;
the deviation is marked), commit on `work/S9-multi-client`, merge. Then a
bounded hour on the one failing check, "a second player arrives": it fails
the same way in `build/S4_play.log`, before any of this work. The partner
logs in, writes its position, the probe `[go`es two tiles off and never sees
a mobile named Guomate in 240 x 30 frames. Read `TradePartner.cs` and the
wait loop in `InputProbe.cs` (~1560–1610) side by side; the likely suspects
are the partner quitting before the probe arrives, or the name never being
asked for because the partner is not `Innocent` on this shard. If it is not
found in the hour, log what was ruled out and move on.

**Check:** `multi_client.bat` exit 0, or exit 1 with only that check failing
and the log entry saying why.

## S10 — Circle of transparency centred on the player

**Why:** review pass 2 finding 1, CONFIRMED, high: the shader's
`circle_of_transparency_center` uniform is never set, so the hole is cut
around the canvas origin. Finding 5 (sprite-path `land_light` 1.0 instead of
0.85355339) is a one-line shader edit in the same files. The only night-2
item a player notices at once.

**Do:** set `UltimaBatcher2D.CircleOfTransparencyCenter` where the radius is
set (`GameScene.cs` ~1010–1016), to the player's screen position the way
upstream's `PixelPos * Viewport * 0.5` resolves. Then settle the SUSPECTED
half: upstream divides the radius by `Camera.Zoom` because it measures in
world-target pixels; if the port measures after zoom, drop the division.
Fix `land_light` in `uo_hue.gdshader` and `uo_hue_blend.gdshader`.

**Check:** scripted `--play` run with `use_circle_of_transparency` on, in
britain-interior, screenshots at zoom 1.0 and 2.0 — the hole is on the
character and the same size in tiles at both zooms. `ab_compare.bat` on
britain-interior with the circle on both sides. `render_probe.bat`.

## S11 — Trees over roofs (F3, F4)

**Why:** both are statics in the same CPU-sorted list, so this is a
`CalculateDepthZ` / `PriorityZ` or roof-foliage branch difference in
`GameSceneDrawingSorting.cs` (~963–975), not the land ordering of F1. Two
places show it; the tester README lists it as known.

**Do:** diff `GameSceneDrawingSorting.cs` against
`sources/ClassicUO/src/ClassicUO.Client/Game/Scenes/GameSceneDrawingSorting.cs`
line by line for the roof, foliage and `IsFoliage`/`Roof` flag paths, then
`AddTileToRenderList` for the depth key. If the code matches, the difference
is in what feeds it: `TileDataLoader` flags, or `PriorityZ` for the
building's floors. Instrument one frame at britain-street: dump the sorted
list around the circled tree and the roof with their depth keys, on both
sides if the reference build allows it. Fix the one thing that differs; mark
it.

**Check:** `ab_compare.bat` on britain-street and britain-shop: trees behind
the roofs; minoc-town, yew-forest and despise-mouth still match.
`multi_client.bat`.

## S12 — Below-ground statics through the street (F1): prepare the decision

**Why:** F1 needs an ADR-0004 amendment and the user's choice between (a)
land through the sorted list, (b) pull affected land tiles out of the bake,
(c) drop statics whose top is below the ground in front. That choice is not
overnight's to make, but it can be made in five minutes in the morning if the
evidence is ready.

**Do:** implement (b) and (c) on two branches, `work/S12-F1-b` and
`work/S12-F1-c`, each small and marked. For each: the eight A/B places plus
four more with foundations and cellars (Britain's bank district, the Trinsic
walls, a Moonglow house, Vesper's bridges), frame time from the effects
probe's `RENDER_FRAME_WORLD` numbers before and after, and a paragraph on
what each approach cannot handle. Draft the amendment as
`docs/architecture/ADR-0004-amendment-draft.md` with both options and the
numbers. Merge nothing. Also answer the open question in F1: replay the
09-21 clean screenshot's position and check whether the cull hides the plinth
from there.

**Check:** two branches that build and pass `mesh_probe.bat`, twelve `ab.png`
per branch, the draft on `main` only if it is a doc.

## S13 — Ground missing at the viewport edges (F2)

**Why:** black triangles at britain-coast's left and top edges. Suspected:
`visibleChunks` in `RenderLists.DrawRenderLists` is tighter than upstream's
per-tile draw at the border, since `_minPixel`/`_maxPixel` match upstream.

**Do:** log the chunk set and the tile range for one frame at britain-coast;
compare with upstream's `GetViewPort` bounds. Widen the chunk set by the
border upstream draws, or, if the meshes are right and the cull is per tile,
find the tile the mesh omits. Mark it.

**Check:** `ab_compare.bat` britain-coast: no black at the edges; a scripted
walk along the coast with `--shot-after` every 100 frames shows none either.
`mesh_probe.bat`.

## S14 — Chunk meshes that rebuild every frame (review 3 and 6): measure, then fix

**Why:** `MeshLayer.SetVisible` marks a layer dirty on every alpha write, so
every fading sprite and every circle-flagged static rebuilds its whole layer
each frame, with four `ToArray()` copies and a GPU buffer reallocation per
run. With the circle on, that is the chunks around the player every frame.
Finding 6: the `ArrayMesh`es are dropped without `Dispose()`.

**Do:** measure first, as S7 did: a scripted run with the circle on in
britain-street, `RENDER_FRAME_WORLD` and GC collections over 600 frames, and
the same with the circle off. If the rebuilds cost over 1 ms or a GC per
second, patch only the colour array of the run that changed
(`SurfaceUpdateAttributeRegion` or a rebuilt colour array on the existing
surface) and remove the `ToArray()` copies. Dispose meshes and atlas pages in
`MeshLayer.Dispose` either way.

**Check:** numbers before and after in the log; `mesh_probe.bat`,
`batcher_probe.bat`; `endurance.bat` five minutes with the circle on holds
static memory within 20 MB and frame time flat.

## S15 — Small parity and hygiene items from review pass 2

Each its own commit on `work/S15-pass2-small`.

- Finding 15: add `PORT DEVIATION` markers to every unmarked edit the review
  lists. No behaviour change.
- Finding 8: `SetWindowTitle` from a plugin thread reaches `DisplayServer`
  off the main thread. Marshal it with `CallDeferred`.
- Finding 10: release the COM references in `PluginClrHost`.
- Launchers: give `common.bat` a `UO_SKIP_GODOT_CHECK=1` mode and make
  `fetch_godot.bat`, `sync_upstream.bat` and `00_bootstrap.bat` call it the
  normal way with that set. No launcher derives `UO_ROOT` itself afterwards.
- Findings 2 and 4 (stretched-land normal per pixel, land light in 8 bits):
  do not fix. Write the two-paragraph ADR-0004 note that records the
  interpolation change and its measured size, and fix the probe to test
  interior texels, as the review's follow-up 2 asks.

**Check:** build, smoke, `plugin_probe.bat`, a fresh-clone dry run of
`00_bootstrap.bat` in a scratch folder prints no FATAL.

## S16 — Tester zip, proven this time

**Why:** S8's log says "not proven": no screenshot of the login screen, and
the fresh cache was not created in 90 s. Night 2 also changes what the
testers get (S10, S11).

**Do:** last sprint of the night, from `main` after everything else merged.
Re-export, unzip to a scratch folder with a fresh `UO_CACHE_DIR`, run
`GUO.bat --console` for as long as the cache takes, screenshot the login gump
with `screenshot.bat` pointed at the unzipped build, then a second screenshot
in the world on the local shard. README: update the known-issues list from
what S11 and S12 left.

**Check:** `build/release/GUO-test-<date>-<sha>.zip`, two screenshot paths
in the log entry, the cache folder listed with its size.

## Night 2 — decisions for the morning

- F1: option (a), (b) or (c) from S12's draft amendment.
- Whether `multi_client.bat` replaces `playtest.bat` as the pre-commit check
  in `CLAUDE.md`, or sits beside it.
- Whether the trade-partner check stays in the playtest if S9 cannot fix it,
  or is moved to a lane of its own that is allowed to fail.

## Deferred, not for overnight

- CI gate. A GitHub workflow needs the 1.2 GB Godot mono engine and the
  templates cached; decide hosting first.
- The lighting spike (`spike/2d-lighting`): steps 3–4 of the design (shadows,
  coloured lights) wait on your look at steps 1–2.

## Log

(append below)

### 2026-09-23 00:05 — S1 plugin host: packets that grow — merged

- Branch `work/S1-plugin-grow`, commit 4d5292c, merged to `main` (--no-ff).
- Correction to the sprint text: upstream's bootstrap (`ClassicUO.Bootstrap/src/Program.cs:337`)
  also rents `length` bytes, not a fixed large buffer, so there was no size to match. The fix
  instead treats the incoming length as the capacity: `tools/plugin_host/src/Program.cs`
  `PacketInPlugin`/`PacketOutPlugin` drop a grown packet with `[plugin_host] WARN ...` and leave
  the client's bytes untouched; `Plugin.cs` no longer throws when a legacy plugin returns a longer
  array; `godot/GUO/src/Client/PluginHost.cs` `PacketIn`/`PacketOut` check the returned length
  and `Log.Warn`. All marked PORT DEVIATION.
- Probe: the managed test plugin grows the first 0x73 ping each way once in the world (+64 bytes).
  `launchers\dev\plugin_probe.bat`: OK — both grown pings dropped with the host warning, player
  kept walking, session closed cleanly. (The session's own input-probe exit code was 1 from
  "a shopkeeper opens a shop" / "a second player arrives" — environmental, unrelated.)
- Razor: the Razor 1.10 build already installed under `%LOCALAPPDATA%\GUO\Data\Plugins\Assistant`
  was used (no need to touch the spike branch). All 19 Razor packet filters enabled in its
  `Profiles\default.xml` (backed up, restored afterwards; Razor re-saved them on close, so they
  were live). `playtest.bat --endure 300`: 18000 frames over 150 walking legs, no crash, no host
  warning (Razor's filters block or rewrite in place; none grew a packet), objects 2426 -> 2305,
  static memory 138.6 -> 157.7 MB, worst frame 72.7 ms. Log: `build\razor_session\session_S1.log`.
  Four input-probe checks failed in that run (someone else on screen could not be hit,
  pathfinder, shopkeeper, second player) — the last two also fail without Razor; the first two
  are worth a look with Razor loaded.
- Build: `-t:Rebuild` 0 errors. `smoke.bat`: OK.

### 2026-09-23 00:35 — S2 selection highlight left in the chunk mesh — merged

- Branch `work/S2-highlight-restore`, commit ad6b3dd, merged to `main`.
- `GameScene.DrawWorld`: the next-frame highlight restore no longer needs `!IsDestroyed`; a
  destroyed object's chunk mesh is marked dirty and rebuilt instead. PORT DEVIATION (ADR-0004).
- New `--highlight-probe` (`src/Bootstrap/HighlightProbe.cs`): hovers a meshed land/static,
  `[go`es 1500 tiles away, checks the object was destroyed, comes back, counts highlighted
  sprites in the reloaded chunk's land and statics layers.
- Honest result: the probe passes **with and without** the fix. A chunk that is dropped and
  reloaded is rebuilt from scratch, so the `[go` scenario in the sprint text never leaves a stray
  highlight. The fix is defensive (covers a mesh that survives its object); no observed bug.
  Logs: `build/S2_highlight.log`, `build/S2_highlight_unfixed.log`; mesh_probe PASS.
- Side effect found later: the probe set `highlight_game_objects: true` in the dev profile and it
  was saved. Under investigation as the trigger for the britain-street draw-order regression (see S6).

### 2026-09-23 00:35 — S3 SDL substitution and audit tiers — merged

- Branch `work/S3-sdl-audit`, commit c208615, merged to `main`.
- `tools/port_audit/run.py`: a non-rewrite file whose port calls `Godot.*` is moved to rewrite
  (records `tier_from_imports`). GameCursor and LoginScene moved shim -> rewrite.
  Tiers now verbatim 247 / 74,760, shim 116 / 56,510, rewrite 37 / 21,959; CLAUDE.md updated.
- `docs/port_plan.md` §2 names the SDL3 -> `GUO.Platform.Sdl` transform. PORT DEVIATION markers
  on GameCursor.SetCustomMouseCursor and MapGump.GetWidth/GetHeight.
- `.gitignore`: `.claude/worktrees/`, `tools/godot/templates/`.
- `03_port_audit.bat`: 396/396 files. `smoke.bat`: OK.

### 2026-09-23 00:35 — S4 renderer and input small fixes — merged

- Branch `work/S4-small-fixes`, five commits, merged to `main`.
- LandView: the stretched texmap path only inside a chunk mesh (flat DrawStatic otherwise);
  GodotInput ignores WheelLeft/Right; dead `_ignoreNextTextInput` removed; ADR-0002 comment at
  SetStencil; `Client` double-start throws instead of Debug.Assert. Deviations marked.
- Not changed: the review's "unused `using Godot;`" in Weather.cs, UOSound.cs and AudioHost.cs —
  all three use Godot types; the finding was wrong.
- render_probe PASS, batcher_probe PASS (130 checks), smoke OK. The scripted play run's failing
  checks (someone on screen, pathfinder, shopkeeper, second player, attack) are the same
  environmental ones as S1.

### 2026-09-23 00:35 — S5 second review pass — merged

- Branch `work/S5-review-pass2`, commit effe688: `docs/review_2026-09-23_pass2.md`.
- Top findings: circle-of-transparency centre uniform never set; stretched-land light per corner
  on CPU; chunk meshes rebuild every frame while fading; land light in 8 bits; sprite-path
  land_light 1.0 (~26% bright); meshes/atlases never disposed; plugin callback issues; three
  launchers bypass common.bat. Not fixed tonight — for triage.
- After merging S2–S5: main `-t:Rebuild` 0 errors, `smoke.bat` OK.

### 2026-09-23 00:43 — S7 blend-state coalescing (measure first) — merged, no coalescing

- Branch `work/S7-blend-measure`, commit fb19b4f, merged to `main`.
- New `--effects-probe N [--effects-plain]` (`src/Bootstrap/EffectsProbe.cs`): spawns N FixedXYZ
  0x36BD effects around the player through `World.SpawnEffect` (the 0x70/0xC0 handlers' path),
  cycling Multiply, Screen, ScreenLess, NormalHalfTransparent, ShadowBlue, respawned every 120
  frames; 600 frames each with none / N / none. `--effects-plain` draws the same with no blend.
- First attempt used Godot's `TimeProcess` monitor: 40 blended effects read +3.2 and +2.3 ms, but
  the same run with no blend read +0.36 and -3.4 ms — noise, not signal. Switched to the client's
  own profiler (`RENDER_FRAME_WORLD`, the world draw where the batcher builds canvas items):

  | run | baseline before / after (ms) | 40 effects (ms) | cost |
  |---|---|---|---|
  | blended 1 | 3.286 / 5.670 | 4.864 | +0.386 |
  | blended 2 | 3.165 / 3.575 | 3.609 | +0.239 |
  | plain 1   | 3.141 / 4.991 | 3.949 | -0.117 |
  | plain 2   | 3.112 / 3.496 | 3.458 | +0.153 |

  Blend states cost roughly 0.1–0.3 ms per frame for 40 effects over the unblended draw, well
  under the 1 ms bar; render GPU time did not rise. **Not coalesced** — per the sprint rule, and
  because coalescing across the default restore would also drop the back-buffer copy between
  overlapping effects (a parity change, not a free win). No "after" numbers since nothing changed.
- blend_probe PASS, smoke OK; endurance not run (batcher untouched). Logs `wt_S7/build/S7_*.log`.
- Also fixed on `work/S7-doc-fix`: S2 had left the trade-partner doc comment on
  `HighlightProbeThenQuit` in `Bootstrap/Main.cs`.

### 2026-09-23 01:30 — S6 parity sweep (merged, fa65352)

- Eight places A/B'd with `launchers\dev\ab_compare.bat`; write-up in `docs/parity_2026-09-23.md`.
- Matches: minoc-town, yew-forest, despise-mouth, britain-interior, the shop gump + bill of sale.
- Findings: F1 below-ground foundation shows through the street (land meshes draw before all
  statics; fix needs an ADR-0004 amendment), F2 black gaps at the viewport edge (britain-coast),
  F3 trees over roofs, F4 an object on a roof (britain-shop). Nothing fixed in the renderer.
- Tool fixes: `focus()` now checks SetForegroundWindow and refuses to type otherwise. Before
  the fix, one pass (~00:57) sent `[go`, `[globallight` and "vendor buy" keystrokes into
  whatever window had focus. Labels on `ab.png` no longer overlap.

### 2026-09-23 06:30 — S8 tester zip (merged, 2a794fe)

- Export was missing `plugin_host`: an export builds for win-x64 and publishes elsewhere.
  `GUO.csproj` now copies the RID folder too and errors on publish if the host is absent.
- Zip: `build/release/GUO-test-2026-09-23-2a794fe.zip` (74.3 MB, 200 files, plugin_host included);
  README notes Razor 1.10 and the known F1/F3 sorting issues.
- Unzipped to a scratch folder with a fresh `UO_CACHE_DIR` and ran `GUO.bat --console`: client
  data valid, all files loaded in 629 ms, plugins loaded (only error: the default Razor.dll path
  is absent in a fresh folder, as in ClassicUO), atlas pages created. No crash in 90 s.
  **Not proven:** no screenshot of the login screen was taken; the fresh cache folder was not
  created in 90 s. Take one screenshot of the login gump before handing the zip out.
