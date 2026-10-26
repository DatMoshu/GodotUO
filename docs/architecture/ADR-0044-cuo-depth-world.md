# ADR-0044: Depth World — the world drawn with ClassicUO's depth rules on RenderingDevice

Date: 2026-10-26. Status: proposed.

**Owner decisions (Moshu, 2026-10-26):**
- The parity reference for this renderer is **ClassicUO's depth semantics**, not GUO's canvas painter's order.
- The Dell quiet host runs OpenGL (Compatibility).
- Other seats' heavy jobs may be **paused for timing windows**.

The supporting review and sprint plan are kept with the project's performance records.

**Layer.** Spike and development happen under `src/Labs/` (L4, `GuoLabs=true`, Debug only). Promotion to an L0 presenter option behind the ADR-0001 seam is a separate owner decision. The canvas batcher stays the complete fallback and the Compatibility-renderer path.

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
   - The renderer reproduces D24 ordering exactly. Where D24S8 is available on the RD device, use it. Otherwise compute the D24 code in the vertex shader and emit `z = code · 2⁻²⁴`, which is exact in float32 and D32F, so comparisons match D24 code comparisons.
   - Sprites are flat (four equal vertex Z values), so interpolation cannot change the code.
   - Acceptance is by **equal-depth class**: for every overlapping pair in the frozen scenes, ClassicUO and Depth World agree on less, equal or greater. Measure this from a ClassicUO depth readback or an instrumented reference build, not by assumption.
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
- Draws: one `DrawListDraw` per category per texture-binding group. The art-view limit rises from 4 to 8 or more, or moves to an array, with nearest sampling. No per-frame resource creation; all RD access on the rendering thread (as ADR-0043).

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

"Zero per-static CPU work on a warm standing frame" is the spike's goal. It is not a claim that every moving-world rule reduces to uniforms.

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

### 6. Acceptance for the spike (go/no-go)

- The depth contract items 1–8 are verified, with the equal-depth class test passing on the frozen static scenes.
- Pixel comparison against ClassicUO captures in the frozen scenes. Every difference from the GUO canvas output is listed and explained.
- Counters show no per-static CPU work on a warm standing frame.
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

- Accepting this ADR.
- Promotion from Labs to an L0 presenter option.
- Default-on for any renderer.
- Resuming the owner-stopped FPS lane.
