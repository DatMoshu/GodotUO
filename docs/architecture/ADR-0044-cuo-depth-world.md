# ADR-0044: Depth World — the world drawn with ClassicUO's depth rules on RenderingDevice

Date: 2026-10-26. Status: **accepted by Moshu 2026-10-26**. Same day, Moshu resumed the FPS lane, with validation on the bank scene only until improvement shows.

**Owner decisions (Moshu, 2026-10-26):**
- The parity reference for this renderer is **ClassicUO's depth semantics**, not GUO's canvas painter's order.
- The Dell quiet host runs OpenGL (Compatibility).
- Other seats' heavy jobs may be **paused for timing windows**.

The supporting review and sprint plan are kept with the project's performance records.

**Production amendment, 2026-10-27.** Moshu authorized the ordinary default
Depth World path on supported RD hardware, with Compatibility and an explicit
manual canvas fallback. The runtime presenter now lives under
`src/Render/DepthWorld/` behind ADR-0001's renderer seam and is built without
`GuoLabs`. Its GLSL is embedded in `GUO.dll`. Experimental probes and unrelated
Labs remain excluded from exports. Main merge/publication remain separately held.
Tonight's shared-load functional comparisons do not pause other projects.

**Layer.** L0 presenter, preserving CUO semantics. `--canvas-world` selects the
ordinary complete canvas renderer; Compatibility selects it automatically.
`--depth-world` remains an explicit diagnostic alias, and never overrides
Compatibility. The normal frame cap is unchanged. The existing extended zoom
defaults to 5 on Depth World; canvas retains its normal range unless `--zoom-max`
is supplied. No profile migration or pack format changes.

## Context

ClassicUO draws the world with a hardware depth buffer:
- Format `Depth24Stencil8` (`GameController.cs:53`).
- `DepthStencilState.Default` (LessEqual, depth write on) set before the world draw (`GameScene.cs:1017`).
- Every vertex carries `CalculateDepthZ()` (`View.cs:35-83`: `(x + y) + (127 + z) * 0.01f`, after the per-direction offset adjustments).
- Land and most statics are drawn from per-chunk vertex buffers (`ChunkMesh`/`MeshLayer`) at depth + 0.5 (`ChunkMesh.cs:321, 426`). Effects are drawn at depth + 1 (`GameEffectView.cs:122`).
- No CPU sort.

GUO draws the world on Godot's 2D canvas, which has no depth test (ADR-0002). To reproduce occlusion, GUO:
- excludes **every** static from the chunk mesh (`ChunkMesh.IsStaticExcludedFromMesh`, PORT DEVIATION);
- sorts all visible objects on the CPU;
- runs `CoverFromBelow` to draw covering land again over sunk objects;
- submits one `RenderingServer` canvas command per sprite.

That deviation's own comment names a depth buffer as the way to have both correctness and speed.

Measured records (`docs/perf/*`, native attribution v3) show the frame is CPU-dominated, with GPU time under about 1 ms. CPU cost grows with visible objects, and visible tiles grow 4× at zoom 2 and 6.25× at zoom 2.5.

ClassicUO's 4K reference shows CUO itself CPU-bound zoomed out: bank zoom 2 at 12.6 ms mean. Its zoom-1 runs were capped at 250 FPS, so its real zoom-1 cost is still unmeasured.

`--gpu-world` (ADR-0007 amendment, `WorldDepthPass.cs`) used a depth buffer to reproduce **GUO's canvas order**:
- a 28-bit tie key;
- covering land in slot 0 and statics in `2*index+1`;
- linear-light blending.

It left 28k–78k-pixel differences and was never timed live. Its failures come from that target, not from depth buffering.

## Decision

Build a world presenter on stock Godot 4.7.2 RenderingDevice, starting from the ADR-0043 code: storage buffers, vertex pulling, the byte-colour target, exact hue shaders and whole-frame canvas fallback. It reproduces ClassicUO's ordering rules as specified below.

### 1. Depth contract

Every item here is a parity requirement. S0 must verify each one against ClassicUO before Depth World pixels are judged.

1. **Depth value.** Vertex Z is ClassicUO's `CalculateDepthZ()` as computed by the ported game objects, in float32, plus +0.5 for meshed land and statics and +1 for effects, exactly where ClassicUO adds them. No other depth key exists on this path.
2. **Projection.** ClassicUO multiplies `Camera.ViewTransformMatrix` by `Matrix.CreateOrthographicOffCenter(0, W, H, 0, short.MinValue, short.MaxValue)` (`Batcher2D.cs:1253-1266`). With the XNA convention this gives `z_ndc = (32768 - depth) / 65535`, so larger depth is nearer and LessEqual lets it win. S0 must confirm:
   - that the camera matrix leaves Z unscaled;
   - which FNA3D driver the reference ran (D3D11, Vulkan or OpenGL), because the clip-space depth convention and the depth-range mapping differ by backend. Record it in the CUO reference manifest.
3. **Format and quantisation.** ClassicUO's effective depth is 24-bit UNORM. D24 and D32F are **not** interchangeable proofs: values about 0.004 depth units apart can quantise together in D24 and stay apart in D32F.
   - **Reference first.** S0 records the reference backend's effective depth format, depth clear value, viewport depth range and clip behaviour at the near and far planes. That comes from the FNA3D driver plus a depth readback. Until then the values below are the expected ones, not established ones.
   - **Expected values:**
     - D24 UNORM; depth cleared to 1.0, the farthest value; viewport depth range [0, 1].
     - No clipping in practice: `CalculateDepthZ` stays far inside ±32767.
     - Reference code = UNORM conversion of `z_ndc`: `round(z_ndc · (2²⁴ − 1))`, with the rounding mode confirmed by readback.
   - **Two encodings, gated separately:**
     - **Native D24S8**, where the RD device offers it.
     - **Emulated D32F:** the vertex shader computes the reference code with the reference arithmetic and emits `z = code · 2⁻²⁴`. That value is exact in float32/D32F, so comparisons equal code comparisons. This is only a faithful ordered encoding **once the reference code construction is known**. By itself it does not reproduce reference projection or rounding.
   - Sprites are flat (four equal vertex Z values), so interpolation cannot change the code.
   - **Acceptance, per encoding:**
     - Synthetic boundary and tie cases: equal depth, ±1 code, values that differ by less than one D24 step, the near and far ends, and the clear value.
     - The **equal-depth class** test on the frozen scenes, against a ClassicUO depth readback or an instrumented reference build: for every overlapping pair, ClassicUO and Depth World agree on less, equal or greater.
4. **Test and write.** LessEqual, depth write on, for every world category including translucent objects. ClassicUO's `DepthRead` line for transparents is commented out (`RenderLists.cs:236-240`).
5. **Discard.** Discard only where the texel alpha is 0 or the hue alpha is 0, as in `IsometricWorld.fx:99-113`. Translucent pixels write depth.
6. **Draw order** (decides equal-depth ties under LessEqual, where the later draw wins). Follows ClassicUO `RenderLists.DrawRenderLists`:
   1. mesh land per visible chunk;
   2. `_tiles`;
   3. `_stretchedTiles`;
   4. mesh statics per visible chunk;
   5. `_statics`, `_animations`, `_effects`;
   6. `_transparentObjects`;
   7. world gump sprites and texts.

   Within a chunk mesh, quads keep ClassicUO's `ChunkMesh` build order. Chunks are visited in ClassicUO's `_visibleChunks` order. Mesh membership uses ClassicUO's own `IsStaticExcludedFromMesh` rules: the GUO deviation is lifted on this path only.
7. **Blending and colour.** Byte-colour RGBA8 UNORM target, not sRGB, starting opaque black. ClassicUO's blend state is reproduced in byte space. Hue, circle-of-transparency and lighting arithmetic follow the ADR-0043 shader contract. Translucency is re-verified against ClassicUO captures, not against the GUO canvas.
8. **Transform, clipping, zoom.**
   - ClassicUO's world render-target size and composition scale per zoom must be confirmed in S0. So must the camera offset, its rounding, and ADR-0001's (-0.25, -0.25) raster sample origin.
   - The scissor equals ClassicUO's world viewport.
   - Integer camera snapping alone is not a parity proof. Full transforms are validated at fractional zoom (at least 0.8, 1.4 and 2.3, plus 0.5, 1, 2 and 2.5) against ClassicUO captures.
   - Resident quads store world-screen positions. The camera offset is applied in the shader with arithmetic proven bit-identical to the CPU path, for example integer offsets subtracted from integer positions below 2²⁴.
9. **No covering land on this path.** The depth test does that job, as in ClassicUO. `CoverFromBelow`, cover runs and the canvas sort are bypassed for objects drawn by Depth World.

### 2. Residency

- Land and mesh statics live in GPU storage buffers per 8×8 chunk. They are built when a chunk is loaded or its revision changes, and are immutable after that.
- Dynamic objects (mobiles, excluded statics, items, effects) are written each frame into one persistently allocated occurrence buffer with one update. The ADR-0043 32-byte record is the starting format.
- Draws: **the original primitive sequence within each category is preserved.** A draw is a contiguous run of primitives whose textures are all bound in the current binding set.
  - A primitive needing a texture outside that set ends the run.
  - Selectors are assigned in first-seen order and are order-preserving.
  - Primitives are **never regrouped by texture**, including across equal-depth or translucent overlaps.
  - The art-view limit rises from 4 to 8 or more, or moves to an array, to make runs longer. Nearest sampling is kept.
  - No per-frame resource creation; all RD access on the rendering thread (as ADR-0043).

### 3. Who owns per-frame state

ClassicUO evaluates these on the CPU each frame. Each stays CPU-owned and sparsely invalidated **until** a GPU equivalent passes an oracle that compares CPU and GPU results every frame in a diagnostic mode.

| State | ClassicUO | Depth World, phase 1 | Possible later step |
|---|---|---|---|
| Visibility (roof / max-Z, draw distance) | Walk sets `SetVisible` + `BuildVisibleIndices` per chunk | Same walk; a per-chunk visibility bitset uploaded only when it changes | Shader predicate from `maxGroundZ`/player-Z uniforms, after the oracle |
| Fade alpha (`ProcessAlpha`, foliage, roofs) | `SetAlpha` on the mesh | Sparse per-instance state buffer, dirty ranges only | — |
| Highlight / selection hue | `SetHue` on the mesh (`GameScene.cs:960-990`) | Same, through the sparse state buffer | — |
| Circle of transparency | Shader radius uniform; gradient type 1 uses CPU alpha | Same split | — |
| Animated art, internal tiles | Excluded from the mesh | Excluded; drawn as dynamic objects | — |
| Picking | CPU, highest `CalculateDepthZ()` under the cursor | Unchanged, CPU-only, bounded by artwork extents in candidate chunks; never reads GPU depth | — |

**Phase 1 (the spike) keeps the CPU visibility walk.** Its goal is **zero per-static packing and submission work** on a warm standing frame: no per-static records, uploads or draw commands, only a bitset write per visible static. The walk is reported separately as gather time. A **zero-walk** goal belongs to a later phase and needs an oracle-proven reuse mechanism for visibility and picking first. This ADR does not claim both at once.

### 4. Fallback and platforms

- Any unsupported command, capability, capacity overflow or failure renders the whole frame on the canvas path (ADR-0043 rules). Never a partial or stale frame.
- RenderingDevice exists on Forward+ and Mobile. **Compatibility (OpenGL) devices keep the canvas path:** web, low-end Android and the Dell quiet host. A GL depth variant would need its own ADR.

### 5. Measurement

- Comparisons use **matched cells only**: same scene, zoom, host, resolution, build mode and metric.
- End-to-end frame p50/p95 is compared with ClassicUO's end-to-end p50/p95. GUO stage CPU times are reported separately and never set against a ClassicUO whole-frame figure.
- The ClassicUO reference must be **truly uncapped**, through an adapter-only path that bypasses the `SuppressDraw`/`Thread.Sleep(1)` limiter without evaluating a modulo by zero. Uncapping is verified from raw frame intervals. `sources/` stays read-only.
- Fullscreen 4K, per the reference policy. The zoom sweep is 0.5, 1, 1.5, 2 and 2.5.
- **Hosts:**
  - The Dell gates the canvas path and the ClassicUO comparison under OpenGL.
  - Depth World is timed on the desktop during **owner-approved quiet windows**: other seats' heavy jobs are paused for windows of at most 10 minutes, coordinated through the lead with `org lease` on `godot:runtime` and `build:D`, and the sample-window host-load gate must still pass.
- Promotion uses the existing AB/BA/AB rule in `fps/EXPERIMENTS.md`.
- **Build modes.** Labs builds are Debug-only today, so the spike compares the Depth World and canvas arms **inside the same Labs build and executable**, switched by a flag. Those results are **provisional**.
  - CUO comparisons and any promotion claim need **matched optimized builds**: an optimized Labs build, or Depth World compiled into Release, measured against Release CUO.
  - A Labs Debug result is never compared directly with a Release reference.
- **Bank-first.** By owner direction, each run validates the bank scene only, at zoom 2 and 2.5, until Depth World shows improvement there. The remaining scenes and zooms follow before any promotion.

### 6. Acceptance for the spike (go/no-go)

- The depth contract items 1–8 are verified, with the equal-depth class test passing on the frozen static scenes.
- Pixel comparison against ClassicUO captures in the frozen scenes shows **zero unexplained differences from ClassicUO**. Separately, every difference from the GUO canvas output is listed and explained.
- Counters show zero per-static packing and submission work on a warm standing frame, with gather reported separately.
- Depth World results are provisional until confirmed with matched optimized builds (§5).
- **Go** if Depth World end-to-end p50 and p95 beat both the GUO canvas and ClassicUO in the matched bank zoom-2 and zoom-2.5 cells. Otherwise stop and report. No speedup is assumed by this ADR.

## Consequences

- Equal-depth ties and translucency follow ClassicUO. Where today's GUO canvas differs from ClassicUO, Depth World output will differ from the canvas. That is intended, and it is documented per scene.
- `--gpu-world` (`WorldDepthPass`, `WorldDepthKey`, `GpuWorld`) is frozen. It is retired once this spike passes.
- The canvas path keeps the PORT DEVIATION in `ChunkMesh` and `CoverFromBelow`. Both remain its correctness mechanism.
- Two renderers must be kept in parity with ClassicUO, not with each other.

## Alternatives considered

- **Keep canvas order and cache static metadata** (the 2026-10-26 handoff). This lowers gather cost but keeps the sort, cover emulation and per-object state on the CPU, so cost still grows with visible statics. Useful on the canvas path; not sufficient to pass ClassicUO zoomed out.
- **Depth buffer reproducing GUO canvas order** (`--gpu-world`). Rejected: unfixed tie differences, Godot 3D node overhead, never timed live.
- **Shared cover material to cut draw calls.** Measured: canvas items fell from 1,608 to 36, but draw calls went from 1,568 to 1,629. Rejected as the main lever.

## Still needs Moshu

- Promotion from Labs to an L0 presenter option.
- Default-on for any renderer.


## Production admission and recovery (2026-10-27)

Capability is checked before the gather may omit resident statics. Unsupported
underlays, HD/water profiles, mirroring, alternate GPU paths, target or render
thread conditions keep the ordinary canvas gather. If capture admission fails
after the gather, the cold queue reconstructs chunk/x/y/linked-object traversal,
including equal-depth ties and covering land, before any object is drawn.

A resident transaction binds its complete source owners before admitting chunks.
On rejection it reconstructs all resident land/statics, including an unvisited
suffix. Captured ordinary commands retain their original object-group IDs;
canvas group order comes from the reconstructed canvas queue. Unsupported raw
commands and subsequent groups retain that same order. Mobile/effect Draw is
never replayed. Flat covering land shares LandView's existing preparation and
exact rectangle math; mesh covering land reuses its baked source. The normal
successful frame builds no cold recovery plan and preserves chunk/zoom reuse.

RD draws resident land, loose land, resident statics, static/animation/effect/
transparent buckets, then world gumps. A texture batch ends at the loose-land
boundary so a later static cannot move before an equal-depth land occurrence.
Original order within each bucket and contiguous resident A/B/A runs is kept.
The hardware comparison remains LessEqual: the later equal-depth draw wins.

Regression instructions: `tools/scenarios/client/depth_world_recovery.md`.
Failure pixels are compared with the ordinary canvas renderer. That establishes
complete fallback, not full CUO pixel parity or live-shard entity correctness.
Existing login-range behavior remains: modern servers without a C8 reply own
entity removals; no timeout guesses a range. Sustained retention on such servers
remains an explicit cross-shard validation limitation.
