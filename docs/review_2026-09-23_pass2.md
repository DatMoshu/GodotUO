# Review 2026-09-23, second pass

The second review pass of 2026-09-23. This pass reads what the first pass
did not: the code named below, the Python tools and every launcher. It fixes
nothing. Findings are ranked most severe first.

- **CONFIRMED**: both sides were read, and the claim follows from the code
  itself, or from arithmetic on it.
- **SUSPECTED**: the code points that way, but the claim depends on runtime
  behaviour or reachability that was not measured.

Paths are relative to the repository root. Port lines are from `main` at
`846fdbc`. Upstream lines are from `sources/ClassicUO` at the reviewed pin.
Nothing was run except `py_compile` and a static read of the launchers. No
client, no shard, no probe.

## What was read

| Port | Upstream counterpart |
|---|---|
| `godot/GUO/src/Render/Camera.cs` (all) | `src/ClassicUO.Renderer/Camera.cs` |
| `godot/GUO/src/Render/MeshLayer.cs` (all) | `src/ClassicUO.Renderer/MeshLayer.cs` |
| `godot/GUO/src/Render/Fonts.cs`, `FontGlyphAtlas.cs`, `SpriteFont.cs` (all) | `src/ClassicUO.Renderer/Fonts.cs`, `FontGlyphAtlas.cs`, `SpriteFont.cs` |
| `godot/GUO/src/Render/shaders/uo_hue.gdshader`, `uo_hue_blend.gdshader`, `uo_hue_mesh.gdshader`, `uo_hue_core.gdshaderinc` (all) | `src/ClassicUO.Renderer/shaders/IsometricWorld.fx` |
| `godot/GUO/src/Network/PluginClrHost.cs` (all) | none (new file). Bootstrap equivalent: `src/ClassicUO.Bootstrap/src/Program.cs` |
| The callback path: `godot/GUO/src/Client/PluginHost.cs` (all), `tools/plugin_host/src/{Program,Plugin,CuoInternal}.cs` (diffed), the callback targets in `godot/GUO/src/Network/Plugin.cs` (diffed) | `src/ClassicUO.Client/PluginHost.cs`, `src/ClassicUO.Bootstrap/src/*.cs`, `src/ClassicUO.Client/Network/Plugin.cs` |

The following were read only as far as needed to confirm a finding:
`UltimaBatcher2D.cs` (uniform setters, `DrawMeshLayer`, `DrawStretchedLand`,
`Encode`), `ChunkMesh.cs` (land writes), `GameScene.cs` and
`GameSceneDrawingSorting.cs` (callers of the mesh API), `LandView.cs`,
`BatcherProbe.cs` (land light check), `TextureAtlas.cs` (`Dispose`),
`GameController.cs` (`GameWindow`), `Client/Main.cs` (host start), ADR-0004
and ADR-0006, and upstream `Batcher2D.cs` and `GameScene.cs`.

---

## Findings

### 1. The circle of transparency is centred on the top-left corner, not on the player. CONFIRMED, high

Port: `godot/GUO/src/Render/shaders/uo_hue_core.gdshaderinc:70`, `:173-176`;
`godot/GUO/src/Render/UltimaBatcher2D.cs:147-150`;
`godot/GUO/src/Game/Scenes/GameScene.cs:1010-1020`.
Upstream: `src/ClassicUO.Renderer/shaders/IsometricWorld.fx:174-177`.

Upstream measures the distance from the centre of the viewport: `PixelPos` is
the clip-space position, so `PixelPos.xy * Viewport * 0.5` is the offset in
pixels from the middle of the world render target. The port replaces that with
`length(screen_pixel - circle_of_transparency_center)`. The uniform defaults to
`vec2(0, 0)`, and the only thing that can set it is the
`UltimaBatcher2D.CircleOfTransparencyCenter` setter. A grep of `godot/` finds no
caller: `GameScene` sets the radius at `:1013` and never sets the centre. So
whenever `UseCircleOfTransparency` is on and `CircleOfTransparencyType != 1`,
the shader cuts its hole around the canvas origin. Statics near the player
that carry the circle flag are either drawn as normal or discarded, depending
on how far they are from the top-left corner. A related point is SUSPECTED:
upstream divides the radius by `Camera.Zoom` because its distance is measured
in world render-target pixels. If the port measures in screen pixels after the
zoom is applied, the same division shrinks the circle twice. That needs a
screenshot with zoom other than 1 to settle.

### 2. Stretched-land lighting is interpolated per vertex, but upstream normalises the interpolated normal per pixel. CONFIRMED, medium

Port: `godot/GUO/src/Render/MeshLayer.cs:124-129`;
`godot/GUO/src/Game/Map/ChunkMesh.cs:503-510`;
`godot/GUO/src/Render/shaders/uo_hue_core.gdshaderinc:94-102`;
`godot/GUO/src/Render/shaders/uo_hue_mesh.gdshader:18`.
Upstream: `src/ClassicUO.Renderer/shaders/IsometricWorld.fx:60-69`, `:88`,
`:143-149`.

Upstream passes the normal through as a varying (`OUT.Normal = IN.Normal`). In
the pixel shader it computes `get_light(IN.Normal)`, which evaluates
`normalize(norm)`, the dot product and the `max(...,0)` on the normal as
interpolated at each pixel. The port evaluates `get_light` once per corner on
the CPU and lets the rasterizer interpolate the scalar. Because `normalize`
and `max` are not linear, the two agree at the corners and differ inside the
tile. For example, take a ridge whose two corners have normals
`(-0.5, 0, 0.87)` and `(0.5, 0, 0.87)`. At the midpoint, upstream gets
`normalize((0, 0, 0.87))`, which is `(0, 0, 1)`, so the base is 0.8536. The
port gets a base of 0.8075 at both corners, so 0.8075 at the midpoint too.
With the default `TerrainShadowsLevel` of 15, brightlight is 1.5, and the final
light is 0.8536 upstream against 0.7845 in the port. That makes the port about
8% darker at the midpoint. The `PORT DEVIATION` at `ChunkMesh.cs:503` says the
scalar is "the scalar the normal only ever fed", and ADR-0004 (lines 50-60)
frames the change as lossless. Neither records that the interpolation changed.
The probe cannot catch this: `godot/GUO/src/Bootstrap/BatcherProbe.cs:436-440`
builds its expected value by bilinearly interpolating the per-corner lights,
the port's model, and it checks only corner texels.

### 3. A chunk mesh with any fading or circle-flagged sprite rebuilds every frame, allocating as it goes. CONFIRMED, medium (performance)

Port: `godot/GUO/src/Render/MeshLayer.cs:163-193`, `:213`, `:296-313`,
`:412-430`; `godot/GUO/src/Game/Scenes/GameScene.cs:656-658`;
`godot/GUO/src/Game/Scenes/GameSceneDrawingSorting.cs:593`, `:897`.
Upstream: `src/ClassicUO.Renderer/MeshLayer.cs:241-245`, `:340-356`.

`SetVisible` marks the layer dirty whenever it writes an alpha. That happens
every frame for every static that carries the circle-of-transparency flag, and
for every sprite that is mid-fade. `ResetAlpha`, which `GameScene` calls every
frame, marks the layer dirty again on the frame after. The port's
`BuildVisibleIndices` treats that flag as a reason to rebuild (the marked
deviation at `:308-313`). The rebuild re-creates every run mesh in the layer,
not only the run that changed. Each run costs one `Godot.Collections.Array`,
four `ToArray()` copies of the scratch buffers (`:419-422`), the
marshalling of those into packed arrays, and a `ClearSurfaces` plus
`AddSurfaceFromArrays`, which reallocates the GPU vertex buffer. Upstream
spent one `SetDataPointerEXT` on the same event, and made no managed
allocation. The deviation is marked. The per-frame cost is not, and with the
circle of transparency on it lands on the chunks around the player in every
frame. The garbage pressure and the time spent were not measured.

### 4. Land light is quantised to 8 bits, which moves the flat-tile value that brightlight pivots on. CONFIRMED, low-medium

Port: `godot/GUO/src/Render/MeshLayer.cs:454`, `:271`;
`godot/GUO/src/Render/shaders/uo_hue_mesh.gdshader:18`;
`godot/GUO/src/Render/shaders/uo_hue_core.gdshaderinc:98-101`.
Upstream: `src/ClassicUO.Renderer/shaders/IsometricWorld.fx:64-68`.

The light is sent in `CUSTOM0` as `RGBA8_UNORM`, so 0.85355339 becomes
218/255, which is 0.854902. Upstream's blend is built so that a flat tile comes
out at exactly 0.85355339 whatever the brightlight. With the port's quantised
base and the default brightlight of 1.5, a flat tile comes out at
`0.85355 + 1.5 * 0.00135`, which is 0.85557. On a flat grey texel of 255, that
is 218.2 against upstream's 217.7, so some texel values round one step
brighter. Every other stretched-land value picks up a quantisation error of
up to ±0.002 as well, and brightlight multiplies it by 1.5. The deviation
itself is marked through ADR-0004. The precision loss is not recorded there.

### 5. The sprite-path shaders send `land_light = 1.0`, but upstream's default normal gives 0.85355339. CONFIRMED, low (rarely reached)

Port: `godot/GUO/src/Render/shaders/uo_hue.gdshader:23`;
`godot/GUO/src/Render/shaders/uo_hue_blend.gdshader:98`;
`godot/GUO/src/Render/shaders/uo_hue_core.gdshaderinc:74-78`.
Upstream: `src/ClassicUO.Renderer/Batcher2D.cs:994-1005` (`SetDefaultNormals`),
`IsometricWorld.fx:60-69`.

Every non-stretched sprite that upstream draws carries the normal `(0,0,1)`,
and `get_light` turns that into 0.85355339 for any brightlight. The port's
sprite shaders write 1.0 instead, and the include's comment says that is what
"every other shader writes". A LAND or LAND_COLOR draw through the sprite path
therefore comes out at `1 + 1.5 * 0.1464 - 0.1464`, which is 1.073, against
0.854 upstream: about 26% brighter at the default settings. The chunk mesh
covers land in normal play (`ChunkMesh.cs:329-377`), so the sprite path is
reached only by land that did not go into the mesh. That is
`LandView.cs:80-90` via `DrawStatic` with `SHADER_LAND`. A related crash path
is SUSPECTED: `LandView.cs:67` calls `UltimaBatcher2D.DrawStretchedLand`,
which throws `NotSupportedException` (`UltimaBatcher2D.cs:652-675`), for any
stretched land drawn outside the mesh. `ChunkMesh.cs:335-352` always meshes
stretched land that has a texmap, so this should be unreachable today. It
becomes reachable if a land tile is ever not in a mesh.

### 6. MeshLayer.Dispose drops its ArrayMeshes without disposing them. CONFIRMED (behaviour), SUSPECTED (impact), low

Port: `godot/GUO/src/Render/MeshLayer.cs:492-504`, and the same pattern in
`godot/GUO/src/Render/TextureAtlas.cs:297-309`, which
`godot/GUO/src/Render/FontGlyphAtlas.cs:145-150` calls.
Upstream: `src/ClassicUO.Renderer/MeshLayer.cs:385-389`.

Upstream disposes its vertex buffer deterministically. The port sets the array
slots to `null`, and its remark says there is "no GPU resource left to free"
because an `ArrayMesh` is reference-counted. The C# wrapper of a Godot
`RefCounted` holds a reference until it is `Dispose()`d or finalised, so each
mesh's surfaces keep their vertex buffers until a GC finalises the wrapper.
`ChunkMesh.Clear()` (`ChunkMesh.cs:205-211`) calls this for every chunk that
is unloaded. `TextureAtlas.Dispose` makes the same assumption for its
`ImageTexture` pages. Also, `SoftReset` (`MeshLayer.cs:473-485`) keeps the
run meshes along with their old surfaces while the layer sits in the pool.
How much memory this holds in practice is unmeasured. An endurance run with
the rendering-server memory counters would settle it.

### 7. Plugins never receive SDL events: `Plugin.ProcessWndProc` has no call site. CONFIRMED, low (documented)

Port: `godot/GUO/src/Network/Plugin.cs:727-740`, with no caller anywhere in
`godot/GUO/src`. The host side that is now dead is
`tools/plugin_host/src/Program.cs:319-327`.
Upstream: `src/ClassicUO.Client/GameController.cs:593` (in `HandleSdlEvent`).

ADR-0006 records this at lines 202-206. It is listed here because it sits on
the callback path under review, and because the code carries no
`PORT DEVIATION` or `PORT GAP` marker where upstream makes the call
(`GameController`), only in the ADR. A plugin that installs `OnWndProc`, or a
hosted assistant that relies on `SdlEventFn`, sees nothing.

### 8. `SetWindowTitle` from a plugin thread reaches `DisplayServer` off the main thread. SUSPECTED, low

Port: `godot/GUO/src/Client/PluginHost.cs:232-240` leads to
`godot/GUO/src/Client/GameController.cs:306-324`, which reaches `:927-930`
(`DisplayServer.WindowSetTitle`). The native path is the same, through
`godot/GUO/src/Network/Plugin.cs:171`.
Upstream: `src/ClassicUO.Client/PluginHost.cs` (same function), which calls
`SDL_SetWindowTitle`.

Assistants such as Razor call `SetTitle` from their own UI thread. Upstream
has the same threading, but its callee was SDL. In the port it is Godot's
`DisplayServer`, whose thread-safety guarantees are different and are
documented per method. The same applies to `RequestMove` and `CastSpell`,
which touch game state from the plugin thread, but those are exactly upstream's
behaviour. Not measured.

### 9. Upstream bugs carried faithfully in the callback path. CONFIRMED, informational (parity, do not fix mid-port)

- `reflectionCmd` returns the address of a stack local, which dangles once the
  function returns: `godot/GUO/src/Client/PluginHost.cs:258-262` and
  `:274-275`. Upstream has the identical code in
  `src/ClassicUO.Client/PluginHost.cs`.
- Growing a packet overflows: `tools/plugin_host/src/Program.cs:329-352` rents
  a buffer sized to the original `length` and copies back the plugin's
  possibly larger `length`, while `godot/GUO/src/Client/PluginHost.cs:323-331`
  throws away the length the plugin returns. The host code is byte-for-byte
  upstream's `src/ClassicUO.Bootstrap/src/Program.cs`. Sprint S1 already
  covers this.

### 10. PluginClrHost does not release its COM references. CONFIRMED, low

Port: `godot/GUO/src/Network/PluginClrHost.cs:157-189`.

`ICLRMetaHost` and `ICLRRuntimeInfo`, and `ICLRRuntimeHost` after use, are
never `Release()`d. `TryCreate` runs at most once per process
(`Client/Main.cs:230-241`), so this leaks three references for the life of the
process and nothing more. On the failure path at `:107-113` the bindings block
is freed correctly. The vtable slot numbers at `:60-63` match
`metahost.h`/`mscoree.h` (GetRuntime 3, GetInterface 9, Start 3,
ExecuteInDefaultAppDomain 11). The `Program.Start(string)` signature in
`tools/plugin_host/src/Program.cs:26-28` matches what
`ExecuteInDefaultAppDomain` requires.

### 11. LIGHTS mode reads the GUMP-adjusted hue instead of the raw one. CONFIRMED, low (practically unreachable), unmarked

Port: `godot/GUO/src/Render/shaders/uo_hue_core.gdshaderinc:139-145`, `:167-168`.
Upstream: `src/ClassicUO.Renderer/shaders/IsometricWorld.fx:117-125`, `:165-168`.

Upstream calls `get_colored_light(IN.Hue.x - 1, ...)`, which uses the raw
vertex hue. The port uses `hue - 1.0`, where `hue` may already have been set to
0 by the GUMP branch. The two differ only for mode `LIGHTS + GUMP` (29) on a
texel with red below 0.02, which nothing is known to emit. The port changed
the expression without a marker.

### 12. The vertex shader's quarter-pixel offset is dropped. SUSPECTED, low, unmarked

Port: `vertex()` in `godot/GUO/src/Render/shaders/uo_hue.gdshader:18-24`,
`uo_hue_blend.gdshader:93-99`, `uo_hue_mesh.gdshader:11-19`.
Upstream: `src/ClassicUO.Renderer/shaders/IsometricWorld.fx:84-85`.

Upstream shifts every vertex by `-0.5/Viewport.x` and `+0.5/Viewport.y` in
clip space, which is a quarter of a pixel left and up. The port has no
equivalent and no comment. With nearest sampling, integer-aligned quads at
zoom 1 rasterise identically. At other zoom levels, or for positions that are
not integers, the texel picked at a pixel's centre can differ at quad edges.
Unmeasured. `/parity-check` at zoom other than 1 would settle it.

### 13. Camera: float order of the inverse and of the normalize differ from XNA. SUSPECTED, low, unmarked

Port: `godot/GUO/src/Render/Camera.cs:231` (`AffineInverse`), `:263`
(`Normalized()`).
Upstream: `src/ClassicUO.Renderer/Camera.cs:177` (`Matrix.Invert`), `:209`
(`Vector2.Normalize`).

The forward transform was rewritten carefully to match XNA's operation order.
The comment at `:214-219` records 12,600 points checked. The inverse was not.
`Transform2D.AffineInverse` computes `x.x = z * (1/det)` and the origin as
`basis_xform(-t)`, while FNA's `Matrix.Invert` goes through 4x4 cofactors with
a different order of multiplies. Godot's `Normalized()` divides by the length
where FNA multiplies by the reciprocal. Either can move a result by one ULP,
and after the `(int)` truncation in `Transform` (`:176-177`), that can move
`ScreenToWorld` or `MouseToWorldPosition` by a pixel at an exact boundary, and
the peek offset by one ULP. `Zoom`'s hand-written clamp (`:77-83`) is
equivalent to `MathHelper.Clamp`. It is explained but not tagged
`PORT DEVIATION`.

### 14. The custom-blend shader's destination may be stale for overlapping effect sprites. SUSPECTED, low

Port: `godot/GUO/src/Render/shaders/uo_hue_blend.gdshader:24`, `:103`;
`godot/GUO/src/Render/UltimaBatcher2D.cs:428`.
Upstream: hardware blend under `GraphicsDevice.BlendState`
(`src/ClassicUO.Renderer/Batcher2D.cs:1239`).

`hint_screen_texture` reads the most recent back-buffer copy. Hardware blending
sees every earlier draw, so the hand-written blend equals it only if a copy
is taken before each effect sprite that overlaps an earlier one. The batcher
requests copies, but this pass did not verify how often. The blend
equation itself is correct: the factor and function tables match XNA's `Blend`
and `BlendFunction` in declaration order, and the premultiplied source matches
what the hardware received upstream. The only gap is that `BlendFactor`,
`InverseBlendFactor` and `SourceAlphaSaturation` fall through to 1.0. Only
plugin command lists use those upstream (`Network/Plugin.cs:868-967`), and
that path is a port gap.

### 15. Unmarked edits and restructuring. CONFIRMED, low (rule 2)

- `godot/GUO/src/Render/MeshLayer.cs:277`: `WriteQuadAt` calls
  `MarkVertexDirty()`, which upstream's does not
  (`src/ClassicUO.Renderer/MeshLayer.cs:163-193`). It is needed by the port's
  rebuild model but not tagged.
- `godot/GUO/src/Render/MeshLayer.cs:245`: `depth` is now unused, with no
  comment. `ChunkMesh.WriteStretchedLand` explains the same drop at
  `ChunkMesh.cs:521-524`, but this method does not.
- `godot/GUO/src/Render/MeshLayer.cs:345-364`: `CloseRun` factors out the
  two inline run-closing blocks upstream repeats
  (`src/ClassicUO.Renderer/MeshLayer.cs:274-285`, `:301-312`). It behaves the
  same, and it grows `_runMeshes` alongside `VisibleRuns`, but the
  restructuring will need reconciling on every upstream merge of this file.
- `godot/GUO/src/Render/shaders/*.gdshader*`: the shaders contain no
  `PORT DEVIATION` marker, although they deviate. The circle flag moved to bit
  7 of the mode byte (`uo_hue_core.gdshaderinc:129-133`), the centre became a
  uniform (`:70`), the light comes from `land_light` (`:74-102`), and findings
  11 and 12 above apply. The header prose explains the first three and cites
  ADR-0002 and ADR-0004, but a grep for the marker finds nothing.

### Clean

- **Fonts.cs**: the only changes are the `FontResources` manifest reader,
  which replaces FileEmbed, and the dropped `GraphicsDevice`. Both are marked.
  The eight `LogicalName`s in `GUO.csproj:80-87` match the names read.
- **FontGlyphAtlas.cs**: line-for-line upstream apart from the constructor,
  which is marked. The key packing, both caches and `CreateEntry` are
  identical. It allocates nothing per frame beyond what upstream does. Its
  `Dispose` is covered by finding 6.
- **SpriteFont.cs**: the XNB parse, the DXT3 decode and `MeasureString` are
  identical. The three changes (device, `ImageTexture`, the `SurfaceFormat`
  integer) are marked. `SURFACE_FORMAT_COLOR = 0` matches XNA's
  `SurfaceFormat.Color`. `Vector2` and `Vector3` resolve to Godot's types
  through the project-wide `Compat/GlobalUsings.cs:21-23`, which is a
  project decision rather than an edit to this file.
- **Hue core**: `get_rgb`, the mode constants, the thresholds, the SPECTRAL,
  SHADOW and EFFECT_HUED branches, the circle fade curve and the final
  `color * alpha` match `IsometricWorld.fx` term for term. The
  `blend_premul_alpha` choice matches XNA's `AlphaBlend`. The hue and light
  samplers use `filter_nearest`, as upstream's `PointClamp` does
  (`Batcher2D.cs:1243-1245`).

---

## Python tools: `py_compile`

Every `.py` under `tools/` that is tracked or untracked but not ignored was
compiled (`git ls-files --cached --others --exclude-standard -- tools`). The
ignored trees `tools/godot` and `tools/modernuo/src` were excluded. Output went
to a scratch directory, so no `__pycache__` was written.

| Files | Failures |
|---:|---:|
| 12 | 0 |

The files: `tools/ab_compare/run.py`, `tools/guo/__init__.py`,
`tools/guo/config.py`, `tools/guo/formats.py`, `tools/modernuo/configure.py`,
`tools/plugin_probe/run.py`, `tools/port_audit/run.py`,
`tools/port_bulk/run.py`, `tools/port_errors/run.py`,
`tools/port_triage/run.py`, `tools/sync_upstream/run.py`,
`tools/uodata/run.py`. All compile under Python 3.12.5. `tools/guoasset` has
no Python of its own (README only).

## Launchers

All 32 `.bat` files outside `launchers/_shared/` were checked. For each, the
check was that the first executable line (after `@echo off`, `setlocal` and
comments) is `call "%~dp0..\_shared\common.bat" || exit /b 1`, that no line
holds a drive-letter or UNC path, and that the file is CRLF.

- **29 pass**. The first executable line is exactly that call, with a
  failure check.
- **No hardcoded absolute path** in any launcher. **All CRLF.**

Violations:

| File:line | Finding |
|---|---|
| `launchers/dev/fetch_godot.bat:6-7` | **Does not call `common.bat`.** It re-derives `UO_ROOT` itself (`for %%I in ("%~dp0..\..")`) and calls `_shared\config.bat` directly. That is understandable, because `common.bat:54-58` exits fatally when Godot is missing, which is the one situation this script exists for. It still breaks the rule, and it duplicates `common.bat`'s root logic. A `common.bat` mode that skips the Godot check would remove the need. |
| `launchers/dev/sync_upstream.bat:10-12` | Calls `common.bat`, but with `2>nul` and no failure check, then falls back to its own `UO_ROOT` and `UO_PYTHON` defaults. This is the same situation: it runs from `00_bootstrap` before Godot exists. `common.bat`'s FATAL text goes to stdout, so `2>nul` does not hide it. |
| `launchers/pipeline/00_bootstrap.bat:6` | Same pattern: `call ...common.bat 2>nul`, no check. On a fresh clone it prints `[common] FATAL: Godot not found` and then carries on and fetches Godot, which is confusing to a first-time user. |

---

## Suggested follow-ups (not done here)

1. Set `CircleOfTransparencyCenter` where the radius is set
   (`GameScene.cs:1010-1016`), and confirm the zoom division with a screenshot.
   This is the only finding a player will notice straight away.
2. Decide whether stretched land needs the normal in the vertex, for example
   a second `CUSTOM1` carrying the normal, or whether ADR-0004 should
   record the interpolation change and its measured size (findings 2 and 4).
   Fix the probe to test interior texels and to compute the expected value
   upstream's way.
3. Separate alpha and hue changes from a full mesh rebuild, for example by
   patching the colour array of the affected run only, and remove the
   `ToArray()` copies (finding 3).
4. `Dispose()` the `ArrayMesh`es and atlas textures explicitly (finding 6).
5. Change `uo_hue.gdshader` and `uo_hue_blend.gdshader` to write 0.85355339 for
   `land_light` (finding 5).
