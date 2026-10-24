// SPDX-License-Identifier: BSD-2-Clause
//
// Reimplementation of ClassicUO's UltimaBatcher2D on Godot's canvas item API.
// See docs/architecture/ADR-0002-batcher-on-godot-canvas.md; the reasoning for
// every deviation below lives there rather than being restated here.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using GUO.Compat;

// Godot's Color is what a canvas modulate takes. Compat's is the byte-packed
// XNA one. Both are in scope; say which this file means.
using Color = Godot.Color;

namespace GUO.Renderer
{
    /// <summary>
    /// Draws ClassicUO's sprites. Upstream builds vertex buffers and hands them
    /// to FNA; this builds canvas item commands and hands them to Godot. The
    /// public surface is upstream's, unchanged, so its ~120 call sites port
    /// without edits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO) — the constructor. Upstream takes a
    /// <c>GraphicsDevice</c>; Godot has none. It takes the canvas item every
    /// sprite hangs under instead, which is the equivalent handle.
    /// </para>
    /// <para>
    /// PORT DEVIATION (GUO) — the pipeline-state setters. <c>SetBlendState</c>
    /// and <c>SetSampler</c> are real, on GUO's own <see cref="BlendState"/>
    /// and <see cref="SamplerState"/> rather than FNA's; see ADR-0003 for the
    /// first. <c>SetStencil</c> is accepted and ignored, because Godot's 2D
    /// canvas has no depth buffer and ADR-0001 keeps the sorting upstream of
    /// here — <see cref="DepthStencilState"/> has the argument.
    /// <c>EnableScissorTest</c> is still absent: nothing in the port calls it,
    /// and a missing method is a compile error at the call site that needs it,
    /// which is better than one that silently does nothing.
    /// </para>
    /// <para>
    /// The <c>depth</c> argument every draw takes is a sort key, not a Z write
    /// — Godot's 2D canvas has no depth buffer and paints in submission order.
    /// ADR-0001 keeps <c>RenderLists</c>, which already sorts by
    /// <c>CalculateDepthZ()</c> before anything reaches here, so submission
    /// order is the sorted order and the argument is accepted and ignored.
    /// </para>
    /// </remarks>
    public sealed partial class UltimaBatcher2D : IDisposable
    {
        private const string SHADER_PATH = "res://src/Render/shaders/uo_hue.gdshader";
        private const string BLEND_SHADER_PATH = "res://src/Render/shaders/uo_hue_blend.gdshader";
        private const string ADD_SHADER_PATH = "res://src/Render/shaders/uo_hue_add.gdshader";
        private const string MESH_SHADER_PATH = "res://src/Render/shaders/uo_hue_mesh.gdshader";
        private const string LAND_ARRAY_SHADER_PATH = "res://src/Render/shaders/uo_hue_land_array.gdshader";
        private const string SPLAT_SHADER_PATH =
            "res://src/Render/shaders/splat_2d.gdshader";
        private const string LAYER_SHADER_PATH =
            "res://src/Render/shaders/layer_2d.gdshader";

        private static readonly float[] _cornerOffsetX = new float[] { 0.0f, 1.0f, 0.0f, 1.0f };
        private static readonly float[] _cornerOffsetY = new float[] { 0.0f, 0.0f, 1.0f, 1.0f };

        // A quad as two triangles, in upstream's winding (GenerateIndexArray).
        private static readonly int[] _quadIndices = { 0, 1, 2, 1, 3, 2 };

        private readonly Rid _parent;

        /// <summary>Where the current batch lands (the screen host, or a render target's canvas item).</summary>
        internal Rid CurrentCanvasItem => _target;

        // Where draws currently land: the screen host, or a render target's
        // canvas item while one is set.
        private Rid _target;
        private RenderTarget2D _currentTarget;
        private readonly ShaderMaterial _material;

        // One material per distinct non-default blend state. There are five in
        // the whole client, so this never grows.
        private readonly Dictionary<string, ShaderMaterial> _blendMaterials =
            new Dictionary<string, ShaderMaterial>();

        private Shader _blendShader;
        private Shader _addShader;

        // The world mesh reads its land light from CUSTOM0, which only exists
        // on a mesh, so it needs a shader of its own. See ADR-0004. Built with
        // the others rather than on first use: every uniform below is global
        // state pushed through SetOnAllMaterials, and a material that appears
        // later has already missed the pushes.
        private readonly ShaderMaterial _meshMaterial;

        /// <summary>The gaussian-splat shader: no texture, no hue uniforms,
        /// so it is built with the others and never pushed to.</summary>
        private readonly ShaderMaterial _splatMaterial;

        /// <summary>The terrain-layer shader: plain textured quad times
        /// vertex colour, like the splat one (built once, never pushed to).</summary>
        private readonly ShaderMaterial _layerMaterial;

        // _currentMaterial is what SPRITES draw under, which SetBlendState
        // chooses. _nextMaterial is what the next item created will carry, and
        // _itemMaterial what the current one does: the mesh path swaps to its
        // own shader and back, and an item can only have one material, so the
        // swap has to cut. Tracking what the item already has is what keeps a
        // run of chunk meshes in a single item instead of two per chunk.
        private ShaderMaterial _currentMaterial;
        private ShaderMaterial _nextMaterial;
        private ShaderMaterial _itemMaterial;

        // Every draw goes into a canvas item. A new one is started whenever a
        // clip opens or closes, because a Godot canvas item paints its own
        // commands before any of its children -- so interleaving clipped and
        // unclipped work inside one item would reorder it. Items are pooled
        // and reused frame to frame; the pool index doubles as the draw index,
        // which is what makes a later item paint over an earlier sibling.
        private readonly List<Rid> _items = new List<Rid>();
        private int _itemCount;

        // PERF (2026-10-22 traces): NewItem sent eight RenderingServer calls
        // per pooled item per frame (parent, transform, clip, custom rect,
        // material, back-buffer copy, filter, draw index) and Cut a ninth,
        // 8-11% of the main thread at ~430 items a frame (1800 zoomed out).
        // Items come back in the same order frame to frame, so each one
        // remembers the state the server holds for it and only a change is
        // sent. The draw index is the pool index and is set once, at creation.
        private struct ItemState
        {
            public Rid Parent;
            public Transform2D Transform;
            public bool Clip;
            public bool CustomRect;
            public Rect2 CustomRectValue;
            public Material Material;
            public bool CopyToBackbuffer;
            public RenderingServer.CanvasItemTextureFilter Filter;
            public RetainedCanvasCommands Commands;
            public bool RetainedHidden;
        }

        private ItemState[] _itemStates = new ItemState[64];

        /// <summary>A filter no item has been given, so the first frame sends one.</summary>
        private const RenderingServer.CanvasItemTextureFilter NoFilter = (RenderingServer.CanvasItemTextureFilter)(-1);

        private readonly List<Rid> _clipStack = new List<Rid>();
        private Rid _current;

        private bool _started;
        private Vector2 _worldOffset;
        private SamplerState _sampler = SamplerState.PointClamp;
        private bool _hdSpriteSampler;

        // Reused by the quad path so a rotated or mirrored sprite does not
        // allocate four arrays per draw.
        private readonly Vector2[] _quadPoints = new Vector2[4];
        private readonly Vector2[] _quadUVs = new Vector2[4];
        private readonly Color[] _quadColors = new Color[4];

        public UltimaBatcher2D(Rid parentCanvasItem)
        {
            _parent = parentCanvasItem;
            _target = parentCanvasItem;

            var shader = GD.Load<Shader>(SHADER_PATH);

            _material = new ShaderMaterial { Shader = shader };
            _meshMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(MESH_SHADER_PATH) };
            _splatMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(SPLAT_SHADER_PATH) };
            _layerMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(LAYER_SHADER_PATH) };
            // Epic B (--merged-land=array): the land mesh shader on a Texture2DArray.
            _landArrayMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(LAND_ARRAY_SHADER_PATH) };
            _currentMaterial = _nextMaterial = _material;
        }

        /// <summary>
        /// hues.mul as a texture: 16 palettes across, 1024 down, 32 entries
        /// each. Until this is set every hued sprite samples nothing.
        /// </summary>
        public Texture2D HueTexture
        {
            set => SetOnAllMaterials("hue_texture", value);
        }

        /// <summary>The coloured-light table, sampled by SHADER_LIGHTS.</summary>
        public Texture2D LightTexture
        {
            set => SetOnAllMaterials("light_texture", value);
        }

        /// <summary>
        /// Where the circle of transparency is centred, in canvas pixels. Not
        /// upstream's: its shader derives this from the viewport, which Godot
        /// gives the shader in different terms.
        /// </summary>
        public Vector2 CircleOfTransparencyCenter
        {
            set => SetOnAllMaterials("circle_of_transparency_center", value);
        }

        public int TextureSwitches, FlushesDone;
        public int NativeTextureMeasurements;
        public static int LastNativeTextureMeasurements;

        /// <summary>Canvas draw commands added this frame: one per sprite, mesh or triangle list.</summary>
        public int Commands;

        /// <summary>
        /// This frame's commands by kind -- plain rects, rects under a
        /// transform (shadows, mirrors), meshes, triangle lists -- and an
        /// estimate of the batches Godot's canvas renderer makes of them: a new
        /// batch at a new item, a change of kind or texture, every mesh and
        /// every transformed rect. For the perf probe (Epic B, B4): when the
        /// estimate tracks the renderer's draw calls, the kinds say what breaks
        /// its batches.
        /// </summary>
        public static int[] Kinds = new int[5];  // 4: covering-land tile meshes (DrawMeshSprite)
        public static int EstimatedBatches;

        /// <summary>Runs of consecutive covering-land meshes: what one mesh per run would cost (B4 fix 2c).</summary>
        public static int CoverRuns;
        public static (int Rects, int Affine, int Meshes, int Triangles, int Batches, int CoverMeshes, int CoverRuns) LastKinds;
        private int _lastKind = -1;
        private Rid _lastKindItem, _lastKindTexture;

        private void Count(int kind, Rid texture)
        {
            Kinds[kind]++;
            if (kind == 4 && _lastKind != 4)
            {
                CoverRuns++;
            }

            if (kind == 1 || kind == 2 || kind == 4 || kind != _lastKind || _current != _lastKindItem || texture != _lastKindTexture)
            {
                EstimatedBatches++;
            }

            _lastKind = kind;
            _lastKindItem = _current;
            _lastKindTexture = texture;
        }

        /// <summary>
        /// The previous frame's counters, kept by BeginFrame before it resets
        /// them, for the perf probe (src/Bootstrap/PerfProbe.cs): texture
        /// switches, canvas items opened (every one is a batch break -- a new
        /// material, clip, target or blend), and draw commands.
        /// </summary>
        public static (int TextureSwitches, int Items, int Commands) LastFrame;

        /// <summary>
        /// PORT DEVIATION (GUO): perf counters (2026-10-24). Why each canvas
        /// item was started last frame (Cut), and how many pooled items exist.
        /// </summary>
        public enum CutReason { Other, Cover, Material, Offset, Sampler, Clip, Begin, Count }
        private readonly int[] _cuts = new int[(int)CutReason.Count];
        public static int[] LastCuts = new int[(int)CutReason.Count];
        public static int LastItemsAllocated;

        /// <summary>Frames begun since start: the perf probe checks every measured frame really drew.</summary>
        public static long FramesBegun;

        /// <summary>
        /// ADR-0007 prototype (Epic B, B2), --batched-world: consecutive quads on
        /// one texture in one canvas item are sent as one triangle array per run
        /// instead of one engine call per sprite. Off by default.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO), for speed alone, and only when switched on: the
        /// same quads, corners and UVs in the same order, so the picture is
        /// meant to be identical (render_diff checks it). A run ends at a texture
        /// change, a new canvas item, any other command on the item, and End.
        /// Texture changes still break runs; ADR-0007's Texture2DArray step would
        /// remove those too.
        /// </remarks>
        public static bool BatchedWorld;

        private Texture2D _runTexture;
        private Rid _runItem;
        private readonly List<Vector2> _runPoints = new();
        private readonly List<Vector2> _runUVs = new();
        private readonly List<Color> _runColors = new();
        private readonly List<int> _runIndices = new();

        /// <summary>Appends one quad (corners TL, TR, BL, BR) to the current run, starting a new run when it must.</summary>
        private void AppendToRun(Texture2D texture, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
                                 Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3, Color modulate)
        {
            FlushCover();
            if (!ReferenceEquals(texture, _runTexture) || _runItem != _current)
            {
                FlushRun();
                _runTexture = texture;
                _runItem = _current;
            }

            int b = _runPoints.Count;
            _runPoints.Add(p0);
            _runPoints.Add(p1);
            _runPoints.Add(p2);
            _runPoints.Add(p3);
            _runUVs.Add(t0);
            _runUVs.Add(t1);
            _runUVs.Add(t2);
            _runUVs.Add(t3);
            _runColors.Add(modulate);
            _runColors.Add(modulate);
            _runColors.Add(modulate);
            _runColors.Add(modulate);
            for (int i = 0; i < _quadIndices.Length; i++)
            {
                _runIndices.Add(b + _quadIndices[i]);
            }
        }

        /// <summary>Sends what is pending: a covering-land run (B4 fix 2c), then the quad run.</summary>
        private void FlushRun()
        {
            FlushCover();
            FlushQuadRun();
        }

        /// <summary>Sends the pending run, if there is one, as one triangle array.</summary>
        private void FlushQuadRun()
        {
            if (_runPoints.Count == 0)
            {
                _runTexture = null;
                return;
            }

            Commands++;
            Count(3, RidOf(_runTexture));
            _canvasCommands?.Bypass();
            RenderingServer.CanvasItemAddTriangleArray(
                _runItem, _runIndices.ToArray(), _runPoints.ToArray(), _runColors.ToArray(), _runUVs.ToArray(),
                null, null, RidOf(_runTexture));
            _runPoints.Clear();
            _runUVs.Clear();
            _runColors.Clear();
            _runIndices.Clear();
            _runTexture = null;
        }

        public void Dispose()
        {
#if GUO_LABS
            _orderedWorld?.Dispose();
            _orderedPresentMaterial?.Dispose();
#endif
            for (int i = 0; i < _items.Count; i++)
            {
                RenderingServer.FreeRid(_items[i]);
            }

            _items.Clear();
            System.Array.Clear(_itemStates);
            _canvasCommands = null;
            _canvasBudget = null;

            foreach (ShaderMaterial material in _blendMaterials.Values)
            {
                material.Dispose();
            }

            _blendMaterials.Clear();
            _meshMaterial?.Dispose();
            _material?.Dispose();

            // PORT DEVIATION (GUO): the land-array material (B4) and the id
            // mirror's canvas item (ADR-0023) are this batcher's too.
            _landArrayMaterial?.Dispose();
            _splatMaterial?.Dispose();
            _layerMaterial?.Dispose();

            if (_idItem.IsValid)
            {
                RenderingServer.FreeRid(_idItem);
                _idItem = default;
            }
        }

        public void SetBrightlight(float f)
        {
            SetOnAllMaterials("brightlight", f);
        }

        public void SetCircleOfTransparencyRadius(float radius)
        {
            SetOnAllMaterials("circle_of_transparency_radius", radius);
        }

        /// <summary>
        /// Every uniform above is global state, and the blend variants draw the
        /// same sprites as the plain material, so all of them have to see it.
        /// Setting only the plain one is how one effect sprite ends up unhued
        /// while everything beside it looks right.
        /// </summary>
        // PORT DEVIATION (GUO): water render modes (ADR-0037). The water
        // uniforms are kept so a blend material made later gets them too.
        private readonly Dictionary<string, Variant> _waterUniforms = new Dictionary<string, Variant>();

        /// <summary>Sets one water uniform (uo_water.gdshaderinc) on every world material.</summary>
        public void SetWaterUniform(string name, Variant value)
        {
            _waterUniforms[name] = value;
            SetOnAllMaterials(name, value);
        }

        /// <summary>
        /// One StringName per uniform name, made once. SetShaderParameter
        /// takes a StringName, and a C# string handed to it builds a new one
        /// on every call (interning, then a native free): the water uniforms
        /// alone did that several times a frame, on every material
        /// (dotnet-trace 2026-10-22: StringName..ctor under SetWaterUniform,
        /// MergedLand.Draw and FlushCover together ~1% of all samples).
        /// </summary>
        private readonly Dictionary<string, StringName> _uniformNames = new Dictionary<string, StringName>();

        private StringName UniformName(string name)
        {
            if (!_uniformNames.TryGetValue(name, out StringName sn))
            {
                sn = new StringName(name);
                _uniformNames[name] = sn;
            }

            return sn;
        }

        private void SetOnAllMaterials(string name, Variant value)
        {
            StringName param = UniformName(name);
#if GUO_LABS
            // Canvas materials are shared: a changed global must also affect the
            // already captured prefix instead of leaving an older RD snapshot.
            if (_orderedWorld?.Deferring == true && !_material.GetShaderParameter(param).Equals(value))
                _orderedWorld.Reject("shared material changed during world capture");
#endif
            GpuWorld.Pass?.SetShared(param, value); // PORT DEVIATION (GUO): --gpu-world's materials share the hue uniforms
            _material.SetShaderParameter(param, value);
            _meshMaterial.SetShaderParameter(param, value);
            _landArrayMaterial.SetShaderParameter(param, value);

            foreach (ShaderMaterial material in _blendMaterials.Values)
            {
                material.SetShaderParameter(param, value);
            }
        }


        // ==========================
        // === Frame ================
        // ==========================

        /// <summary>
        /// Opens a frame. Everything drawn until the next <c>BeginFrame</c>
        /// paints in submission order, across as many Begin/End batches as the
        /// caller wants.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream has no frame call -- FNA's device
        /// clears the back buffer and a SpriteBatch owns nothing between
        /// batches, so Begin is the only boundary it needs. Here the batch is
        /// a pool of canvas items that persist until something clears them,
        /// and a single upstream frame opens the batcher several times over
        /// (the world, the UI, the cursor, the composite in RenderTargets).
        /// Resetting the pool in Begin would erase the previous pass, so the
        /// reset lives here and Begin only sets up its own batch.
        /// </remarks>
        public void BeginFrame() => BeginFrame(deferRetainedHiding: false);

        /// <summary>Opt into deferred retained hiding only when a complete
        /// synchronous frame will call <see cref="EndFrame"/> in a finally block.</summary>
        public void BeginFrame(bool deferRetainedHiding)
        {
            // PORT DEVIATION (GUO): a missing EndFrame must not carry a
            // partially matched old stream into the next frame, even in Release.
            if (_deferredCanvasFrameOpen || _canvasRecoveryPending)
                RecoverDeferredCanvasFrame(throwOnFailure: true);
            _retainCanvasFrame = RetainCanvas;
            int itemsToClear = _items.Count;
#if GUO_LABS
            // ADR-0043: every item used last frame must be cleared once. The
            // unused tail was cleared on its first idle frame and is still empty.
            if (GUO.Labs.GpuRenderer.OrderedWorldCapture.Enabled) itemsToClear = _itemCount;
            if (GUO.Labs.GpuRenderer.OrderedWorldCapture.Enabled) _retainCanvasFrame = false;
            ResetOrderedWorldItems();
#endif
            EnsureNotStarted();
            FinishCanvasCommands();
            // RD owns its late visibility election; legacy GPU capture keeps
            // its existing eager lifecycle. Ordinary embedded/probe callers
            // retain the default behavior without needing an EndFrame call.
            _deferredCanvasFrameOpen = deferRetainedHiding && _retainCanvasFrame && !GpuWorld.Enabled;

            LastFrame = (TextureSwitches, _itemCount, Commands);
            LastNativeTextureMeasurements = NativeTextureMeasurements;
            System.Array.Copy(_cuts, LastCuts, _cuts.Length);
            System.Array.Clear(_cuts);
            LastItemsAllocated = _items.Count;
            LastKinds = (Kinds[0], Kinds[1], Kinds[2], Kinds[3], EstimatedBatches, Kinds[4], CoverRuns);
            System.Array.Clear(Kinds);
            EstimatedBatches = 0;
            CoverRuns = 0;
            _lastKind = -1;
            FramesBegun++;
            _coverRuns.NextFrame();
            _itemCount = 0;
            _sizedTexture = null;
            TextureSwitches = 0;
            NativeTextureMeasurements = 0;
            FlushesDone = 0;
            Commands = 0;

            // Once a frame, before anything draws. Godot cannot upload part of
            // a texture, so TextureAtlas blits sprites into a CPU-side page as
            // they decode and defers the upload to here; see its remarks for
            // what the alternative costs.
            TextureAtlas.FlushAll();

            // Include every item that could still hold previous commands,
            // even when this frame ends up using fewer items.
            for (int i = 0; i < itemsToClear; i++)
            {
                ref ItemState state = ref _itemStates[i];
                if (_retainCanvasFrame && state.Commands?.HasCommands == true)
                {
                    // Main DrawFrame owns an explicit final boundary. Other
                    // callers still hide eagerly and reveal only their used prefix.
                    if (!_deferredCanvasFrameOpen && !state.RetainedHidden)
                    {
                        RenderingServer.CanvasItemSetVisible(_items[i], false);
                        CanvasHides++;
                        state.RetainedHidden = true;
                    }
                }
                else
                {
                    RenderingServer.CanvasItemClear(_items[i]);
                    CanvasOwnerClears++;
                    state.Commands?.Reset();
                }
            }
#if GUO_LABS
            OrderedCanvasItemsCleared += itemsToClear;
#endif
        }

        public void Begin()
        {
            Begin(Transform2D.Identity);
        }

        /// <summary>
        /// Starts a batch whose sprites are all placed through
        /// <paramref name="viewTransform"/> -- the camera's zoom and peek.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream's overload is
        /// <c>Begin(Effect, Matrix)</c>, and every call site passes null for
        /// the effect, FNA's custom-shader slot, which has no analogue and no
        /// caller. The matrix is a <see cref="Transform2D"/> for the reason
        /// <see cref="Camera"/> gives: it is the six components of the sixteen
        /// that upstream ever writes.
        ///
        /// The transform goes on the target's own canvas item rather than on
        /// each sprite, so a clip rect nested under it is transformed with the
        /// content -- which is what upstream's ScissorStack works out by hand.
        /// </remarks>
        public void Begin(Transform2D viewTransform)
        {
            EnsureNotStarted();

            RenderingServer.CanvasItemSetTransform(_target, viewTransform);
            _viewTransform = viewTransform; // PORT DEVIATION (GUO): --gpu-world's composite
            if (_mirroring)
            {
                RenderingServer.CanvasItemSetTransform(_idItem, viewTransform); // PORT DEVIATION (GUO): ADR-0023
            }

            _sizedTexture = null;
            _started = true;
            _worldOffset = Vector2.Zero;
            _clipStack.Clear();
            _currentMaterial = _nextMaterial = _material;
            _sampler = SamplerState.PointClamp;

            _hdSpriteSampler = false;

            Cut(CutReason.Begin);
        }

        public void End()
        {
            EnsureStarted();
            FlushRun();
            FinishCanvasCommands();

            _started = false;
        }

        /// <summary>
        /// Points subsequent draws at a render target, or back at the screen
        /// when passed null.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream says
        /// <c>batcher.GraphicsDevice.SetRenderTarget(target)</c>. There is no
        /// device, and a Godot render target is a SubViewport that renders its
        /// own subtree, so switching target means switching which canvas item
        /// new work is parented to. Upstream only ever switches between
        /// batches, and this has to be called outside Begin/End for the same
        /// reason it already is: items already submitted stay where they were
        /// submitted.
        /// </remarks>
        // PORT DEVIATION (GUO): the post-processing object-id mirror (ADR-0023).
        // Strictly off unless an enabled post-processing pass declares id_tex:
        // then, while the world target is bound, every upright or rotated sprite
        // is also drawn, in the same order, into one canvas item under the id
        // viewport, in a flat colour naming the object being drawn
        // (CurrentObjectId, set by RenderLists). Land is background (id 0), and
        // BatchedWorld runs are not mirrored. None of this is counted in
        // Commands or the texture/flush counters.

        /// <summary>
        /// The object the next sprites belong to, for the id mirror: 0 draws
        /// nothing into it, a negative id paints background over what is below
        /// (land that covers an object).
        /// </summary>
        public int CurrentObjectId { get; set; }

        /// <summary>The id mirror is on for the current target (ADR-0023); --gpu-world leaves the frame to the canvas then.</summary>
        public bool Mirroring => _mirroring;

        private bool _mirroring;
        private Rid _idItem;
        private static ShaderMaterial _idMaterial;
        private readonly Color[] _idColors = new Color[4];
        private readonly Vector2[] _idPoints = new Vector2[4];

        private void BeginIdMirror(RenderTarget2D target)
        {
            var stack = PostFx.PostFxStack.Instance;
            Rid canvas = stack.IdCanvas;
            _mirroring = target != null && canvas.IsValid && ReferenceEquals(target, stack.IdFor);
            if (!_mirroring)
            {
                return;
            }

            _idMaterial ??= new ShaderMaterial
            {
                Shader = new Shader
                {
                    Code = @"shader_type canvas_item;
render_mode blend_disabled;
// ADR-0023: an object's pixels in its id colour, where its sprite is solid.
// The id comes from the vertex colour alone: fragment COLOR is already multiplied by the texture.
varying flat vec3 id;
void vertex() { id = COLOR.rgb; }
void fragment() {
    if (texture(TEXTURE, UV).a < 0.5) { discard; }
    COLOR = vec4(id, 1.0);
}",
                },
            };
            if (!_idItem.IsValid)
            {
                _idItem = RenderingServer.CanvasItemCreate();
                RenderingServer.CanvasItemSetMaterial(_idItem, _idMaterial.GetRid());
                RenderingServer.CanvasItemSetDefaultTextureFilter(_idItem, RenderingServer.CanvasItemTextureFilter.Nearest);
            }

            RenderingServer.CanvasItemSetParent(_idItem, canvas);
            RenderingServer.CanvasItemClear(_idItem);
        }

        private static Color IdColor(int id) =>
            id < 0 ? Colors.Black : new(((id >> 16) & 0xFF) / 255f, ((id >> 8) & 0xFF) / 255f, (id & 0xFF) / 255f, 1f);

        private void MirrorRect(Rect2 rect, Texture2D texture, Rect2 source)
        {
            if (!_mirroring || CurrentObjectId == 0)
            {
                return;
            }

            rect.Position += _itemOffset;
            RenderingServer.CanvasItemAddTextureRectRegion(_idItem, rect, RidOf(texture), source, IdColor(CurrentObjectId), false, false);
        }

        /// <summary>Set around a shadow: shadows are not the object, so they stay background.</summary>
        private bool _idSkip;

        /// <summary>
        /// Covering land (DrawMeshSprite) paints background over what it hides,
        /// or the id buffer would keep the hidden object and outline its hidden
        /// part. The mesh in black: the id shader writes vertex colour times
        /// modulate, which is 0, the background id.
        /// </summary>
        private void MirrorBackgroundMesh(ArrayMesh mesh, Vector2 offset, Texture2D texture)
        {
            if (!_mirroring)
            {
                return;
            }

            RenderingServer.CanvasItemAddMesh(_idItem, mesh.GetRid(), new Transform2D(0f, offset), Colors.Black, texture.GetRid());
        }

        private void MirrorQuad(Texture2D texture)
        {
            if (!_mirroring || CurrentObjectId == 0 || _idSkip)
            {
                return;
            }

            Color c = IdColor(CurrentObjectId);
            for (int i = 0; i < 4; i++)
            {
                _idColors[i] = c;
                _idPoints[i] = _quadPoints[i] + _itemOffset;
            }

            RenderingServer.CanvasItemAddTriangleArray(_idItem, _quadIndices, _idPoints, _idColors, _quadUVs, null, null, RidOf(texture));
        }
        // END PORT DEVIATION (GUO)

        public void SetRenderTarget(RenderTarget2D target)
        {
            EnsureNotStarted();

            _target = target == null ? _parent : target.CanvasItem;
            _currentTarget = target;
            BeginIdMirror(target); // PORT DEVIATION (GUO): ADR-0023's id mirror; see above

            // Upstream's targets are RenderTargetUsage.DiscardContents
            // (GameController.PreparingDeviceSettings), and FNA clears such a
            // target to opaque black whenever it is bound (release-build
            // DiscardColor, GraphicsDevice.SetRenderTargets). The light and UI
            // targets are cleared again straight after; the world target is
            // not, so black is what shows wherever nothing is drawn -- past the
            // edge of the map, say. Left transparent, the tiled window
            // background showed through there instead (parity night
            // 2026-09-26, hythloth).
            if (target != null)
            {
                target.ClearColor = Colors.Black;
            }
        }

        /// <summary>
        /// Fills the current render target with a colour.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream clears through the device, which can
        /// also clear the back buffer. Here the colour is stored on the target
        /// and painted behind everything the batcher draws -- see
        /// <see cref="RenderTarget2D.ClearColor"/>. There is no back buffer to
        /// clear and no caller that wants one: the single upstream site that
        /// cleared it, RenderTargets.Draw, covers the whole window with the
        /// tiled background on the next line.
        /// </remarks>
        public void Clear(Color color)
        {
            if (_currentTarget == null)
            {
                throw new NotSupportedException(
                    "Clear with no render target set: there is no back buffer to clear.");
            }

            _currentTarget.ClearColor = color;
        }

        /// <summary>
        /// Accepted and ignored; see <see cref="DepthStencilState"/> for why
        /// there is nothing to set.
        /// </summary>
        public void SetStencil(DepthStencilState stencil)
        {
        }

        /// <summary>The land mesh shader on LandPages' Texture2DArray (--merged-land=array).</summary>
        private ShaderMaterial _landArrayMaterial;

        private static readonly StringName LandPagesParam = "land_pages";

        /// <summary>The page array <see cref="_landArrayMaterial"/> was last bound to.</summary>
        private Texture2DArray _boundLandPages;

        /// <summary>
        /// Binds <paramref name="pages"/> to the land-array material, once per
        /// array object. LandPages keeps one Texture2DArray and refills it in
        /// place (CreateFromImages replaces the texture under the same RID), so
        /// a material bound to it stays bound through every rebuild; only a new
        /// object (LandPages.Reset, then the first Sync) needs the set again.
        /// Before this, FlushCover set it on every cover flush, which built a
        /// StringName and crossed into Godot each time for nothing.
        /// </summary>
        private void BindLandPages(Texture2DArray pages)
        {
            if (ReferenceEquals(_boundLandPages, pages))
            {
                return;
            }

            _landArrayMaterial.SetShaderParameter(LandPagesParam, pages);
            _boundLandPages = pages;
        }

        /// <summary>
        /// All visible land as one mesh over LandPages' Texture2DArray
        /// (MergedLand, --merged-land=array): the land mesh shader's shading,
        /// one draw call.
        /// </summary>
        public void DrawLandArrayMesh(ArrayMesh mesh, Texture2DArray pages)
        {
            EnsureStarted();
            SelectSpriteSampler(null);
            BindLandPages(pages);
            EnsureMaterial(_landArrayMaterial);
            FlushRun();
            Commands++;
            Count(2, default);
            CanvasMesh(_current, mesh.GetRid(), Transform2D.Identity, Colors.White, default);
        }

        /// <summary>
        /// A gaussian-splat mesh (staged ComfyUI multis) on the batcher's
        /// current item, under the splat material: the same call shape as
        /// DrawLandMesh, but texturless (the shader reads vertex colour, not
        /// a texture) and in tile space (the view transform zooms it).
        /// </summary>
        public void DrawSplatMesh(ArrayMesh mesh)
        {
#if GUO_LABS
            _orderedWorld?.Reject("splat");
#endif
            EnsureStarted();
            SelectSpriteSampler(null);
            EnsureMaterial(_splatMaterial);
            FlushRun();
            Commands++;
            Count(2, default);
            CanvasMesh(_current, mesh.GetRid(), Transform2D.Identity, Colors.White, default);
        }

        /// <summary>
        /// A terrain-layer quad (ComfyUI underlays/overlays) on the batcher's
        /// current item, under the layer shader: plain texture times vertex
        /// colour, normal alpha blending. Same call shape as DrawSplatMesh.
        /// </summary>
        public void DrawLayerMesh(ArrayMesh mesh, Texture2D texture)
        {
#if GUO_LABS
            _orderedWorld?.Reject("terrain layer");
#endif
            EnsureStarted();
            SelectSpriteSampler(null);
            EnsureMaterial(_layerMaterial);
            FlushRun();
            Commands++;
            Count(2, default);
            CanvasMesh(_current, mesh.GetRid(), Transform2D.Identity, Colors.White, texture.GetRid());
        }

        /// <summary>
        /// One merged land mesh (MergedLand, --merged-land): drawn as a run of
        /// DrawMeshLayer is, on the mesh material under the world offset.
        /// </summary>
        public void DrawLandMesh(ArrayMesh mesh, Texture2D texture)
        {
            EnsureStarted();
            SelectSpriteSampler(null);
            EnsureMaterial(_meshMaterial);
            FlushRun();
            Commands++;
            Count(2, default);
            CanvasMesh(_current, mesh.GetRid(), Transform2D.Identity, Colors.White, texture.GetRid());
        }

        /// <summary>
        /// Draws one chunk mesh layer: its visible sprites, already grouped
        /// into one mesh per texture. Returns how many sprites were drawn, as
        /// upstream's caller counts.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream binds the layer's vertex buffer and a
        /// shared index buffer on the device and issues DrawIndexedPrimitives
        /// per run. There is no buffer to bind; MeshLayer has already built one
        /// ArrayMesh per run, because canvas_item_add_mesh takes a single
        /// texture per call. See ADR-0004.
        /// </remarks>
        public int DrawMeshLayer(MeshLayer layer)
        {
            EnsureStarted();
            SelectSpriteSampler(null);

            if (layer == null || layer.VisibleSpriteCount == 0)
            {
                return 0;
            }

            EnsureMaterial(_meshMaterial);

            for (int i = 0; i < layer.VisibleRunCount; i++)
            {
                ref TextureRun run = ref layer.VisibleRuns[i];
                ArrayMesh mesh = layer.GetRunMesh(i);

                if (mesh == null || run.Texture == null)
                {
                    continue;
                }

                // PORT DEVIATION (GUO): HD land runs use the chosen HD
                // filter; native runs keep Nearest.
                bool wantHd = Arts.Art.IsHdTexture(run.Texture);
                SamplerState want = wantHd ? Arts.Art.HdSampler(run.Texture) : SamplerState.PointClamp;
                if (_sampler != want)
                {
                    _sampler = want;
                    Cut(CutReason.Material);
                }

                FlushRun();

                Commands++;
                Count(2, default);

                CanvasMesh(
                    _current,
                    mesh.GetRid(),
                    Transform2D.Identity,
                    Colors.White,
                    run.Texture.GetRid());
            }

            if (_sampler != SamplerState.PointClamp)
            {
                _sampler = SamplerState.PointClamp;
                Cut(CutReason.Material);
            }

            return layer.VisibleSpriteCount;
        }

        /// <summary>
        /// Draws one sprite of a chunk mesh layer, at the world offset given,
        /// in the middle of ordinary sprites.
        /// </summary>
        /// <remarks>
        /// GUO addition, for land drawn again inside the sorted pass
        /// (RenderLists.CoverFromBelow; ADR-0004, amended 2026-09-25). Each
        /// sprite's mesh is kept by its layer and rebuilt only when its quad
        /// changes (MeshLayer.GetSpriteMesh): rebuilt every frame, a sunk mine
        /// floor's two thousand covering tiles cost more than the rest of the
        /// world together (parity night 2026-09-26, P1).
        /// </remarks>
        public int DrawMeshSprite(MeshLayer layer, int index, int offsetX, int offsetY)
        {
            EnsureStarted();

            if (layer == null || index < 0 || index >= layer.Count || layer.Textures[index] == null)
            {
                return 0;
            }

            var offset = new Vector2(-offsetX, -offsetY);
#if GUO_LABS
            if (OrderedWorldRecording && _orderedWorld.MeshRecord(layer, index, offset)) return 1;
#endif

            // PORT DEVIATION (GUO): --gpu-world draws covering land in the
            // pass, keyed; a tile whose texture has no land-array page (HD,
            // themed) is left to the bake, counted as a known deviation.
            if (_capture != null)
            {
                Texture2D coverTexture = layer.Textures[index];
                int coverPage = LandPages.LayerOf(coverTexture);
                if (coverPage < 0)
                {
                    CoverSkipped++;
                    return 0;
                }

                _capture.AddCoverTile(layer, index, coverPage, LandPages.ScaleOf(coverTexture), offset,
                    CaptureDepthAbs, CaptureTieAbs, CaptureS);
                return 1;
            }

            // B4 fix 2c: held back and drawn with the tiles that follow it as
            // one mesh, unless the id mirror needs each tile's own texture.
            if (MergedCover && !_mirroring)
            {
                Texture2D t = layer.Textures[index];
                int page = LandPages.LayerOf(t, out Vector2 scale);
                if (page >= 0)
                {
                    if (_cover.Count > 0 && _coverOffset != offset)
                    {
                        FlushCover();
                    }

                    FlushQuadRun();
                    _coverOffset = offset;
                    _cover.Add((layer, index, page, scale));
                    return 1;
                }
            }

            FlushCover();
            ArrayMesh mesh = layer.GetSpriteMesh(index);

            // PORT DEVIATION (GUO): HD covering tiles use the chosen filter;
            // native keeps Nearest.
            bool isHdCover = Arts.Art.IsHdTexture(layer.Textures[index]);
            SamplerState wantCover = isHdCover ? Arts.Art.HdSampler(layer.Textures[index]) : SamplerState.PointClamp;
            bool samplerSwap = _sampler != wantCover;
            SamplerState keepSampler = _sampler;
            if (samplerSwap)
            {
                _sampler = wantCover;
            }

            // The same route DrawMeshLayer takes: the offset on the item, the
            // mesh drawn untransformed. A run of covering tiles shares one item.

            if (!ReferenceEquals(_itemMaterial, _meshMaterial) || _itemOffset != offset || samplerSwap)
            {
                Vector2 keep = _worldOffset;

                _worldOffset = offset;
                _nextMaterial = _meshMaterial;
                Cut(CutReason.Cover);
                _nextMaterial = _currentMaterial;
                _worldOffset = keep;
            }
            FlushRun();

            Commands++;
            Count(4, default);

            CanvasMesh(
                _current,
                mesh.GetRid(),
                Transform2D.Identity,
                Colors.White,
                layer.Textures[index].GetRid());
            MirrorBackgroundMesh(mesh, offset, layer.Textures[index]); // PORT DEVIATION (GUO): ADR-0023

            if (samplerSwap)
            {
                _sampler = keepSampler;
                Cut(CutReason.Cover);
            }

            return 1;
        }

        /// <summary>
        /// Epic B, B4 fix 2c (--merged-cover): consecutive covering-land tiles,
        /// held back by DrawMeshSprite and drawn as one mesh over LandPages'
        /// Texture2DArray by FlushCover -- which every other draw reaches
        /// first, through FlushRun or AppendToRun, so the order is unchanged.
        /// </summary>
        public static bool MergedCover;
        private readonly List<(MeshLayer Layer, int Index, int Page, Vector2 Scale)> _cover = new();
        private Vector2 _coverOffset;
        private long _coverSynced = -1;
        private readonly CoverRuns _coverRuns = new();

        private void FlushCover()
        {
            if (_cover.Count == 0)
            {
                return;
            }

            // Once a frame (re-uploaded pages), and again whenever a tile brought a new page.
            if (LandPages.Dirty || _coverSynced != FramesBegun)
            {
                LandPages.Sync();
                _coverSynced = FramesBegun;
            }

            Rid mesh = _coverRuns.Get(_cover);
            _cover.Clear();

            // The item state is put back as it was found. A caller checks its
            // material and offset (EnsureMaterial, SetWorldOffset) before it
            // flushes, then draws into whatever item is current; left on the
            // land-array item, or on the plain path's mesh item at the covering
            // offset, the next sprite was drawn with the wrong shader and
            // offset (measured: roof tiles missing, 10-60k px). This runs
            // inside Cut too, so the pending material and offset are kept.
            ShaderMaterial itemMaterial = _itemMaterial ?? _currentMaterial, next = _nextMaterial;
            Vector2 itemOffset = _itemOffset, world = _worldOffset;
            bool cut = !ReferenceEquals(_itemMaterial, _landArrayMaterial) || _itemOffset != _coverOffset;
            if (cut)
            {
                _worldOffset = _coverOffset;
                _nextMaterial = _landArrayMaterial;
                Cut(CutReason.Cover);
            }

            FlushQuadRun();
            BindLandPages(LandPages.Array);
            Commands++;
            Count(4, default);
            CanvasMesh(_current, mesh, Transform2D.Identity, Colors.White, default);

            if (cut)
            {
                _worldOffset = itemOffset;
                _nextMaterial = itemMaterial;
                Cut(CutReason.Cover);
            }

            _nextMaterial = next;
            _worldOffset = world;
        }

        /// <summary>
        /// Submits a cached static quad in painter's order. Static quads are
        /// axis-aligned with uniform hue/alpha, so they need neither rebuilt
        /// ArrayMeshes nor the per-vertex lighting used by stretched land.
        /// </summary>
        public int DrawStaticMeshSprite(MeshLayer layer, int index, int offsetX, int offsetY)
        {
            if (index < 0 || index >= layer.Count || !layer.Visible[index] || layer.Textures[index] == null)
                return 0;

#if GUO_LABS
            if (OrderedWorldRecording && CaptureOrderedStatic(layer, index, offsetX, offsetY)) return 1;
#endif

            ref var quad = ref layer.Vertices[index];
            var uvSize = quad.TextureCoordinate3 - quad.TextureCoordinate0;
            var size = quad.Position3 - quad.Position0;
            AddSprite(layer.Textures[index],
                quad.TextureCoordinate0.X, quad.TextureCoordinate0.Y, uvSize.X, uvSize.Y,
                quad.Position0.X - offsetX, quad.Position0.Y - offsetY, size.X, size.Y,
                quad.Hue0, 0f, 0f, 0f, 1f, 0f, 0);
            return 1;
        }

        private Vector2 _itemOffset;

        // PORT DEVIATION (GUO): --gpu-world (ADR-0007, GPU-02). While the
        // sorted world pass is captured, every sprite the batcher would put
        // on the canvas becomes an instance of the world depth pass instead,
        // with the key RenderLists set for the object being drawn. A custom
        // blend (ADR-0003's read-back equations) cannot run inside the 3D
        // pass; those sprites keep the canvas, and land after the pass's
        // composite because the composite item was opened first.
        private WorldDepthPass _capture;
        private bool _captureSuspended;
        private Transform2D _viewTransform = Transform2D.Identity;
        private static CanvasItemMaterial _compositeMaterial;

        /// <summary>The key parts of the object being drawn, set by RenderLists before each Draw.</summary>
        public uint CaptureDepthAbs, CaptureTieAbs;
        public int CaptureS;

        /// <summary>Covering tiles the pass could not take (no land-array page); a known deviation, counted.</summary>
        public static int CoverSkipped;

        public bool Capturing => _capture != null;

        /// <summary>
        /// Opens the composite item for the pass's texture, then routes the
        /// sprites that follow into the pass. The item is a child of the
        /// view-transformed target item under the inverse view, so the
        /// texture lands on the target's own pixels; the view is a scale and
        /// a translation, and the round trip is well under a pixel.
        /// </summary>
        public void BeginCapture(WorldDepthPass pass, int width, int height)
        {
            EnsureStarted();
            FlushRun();
            _compositeMaterial ??= new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.PremultAlpha };

            Rid item = NewItem(CurrentParent, _viewTransform.AffineInverse());
            _canvasCommands?.Bypass();
            ItemSetMaterial(item, _compositeMaterial);
            RenderingServer.CanvasItemAddTextureRect(item, new Rect2(0, 0, width, height), pass.Texture.GetRid());
            Commands++;

            Cut();
            _capture = pass;
            _captureSuspended = false;
        }

        public void EndCapture()
        {
            FlushRun();
            _capture = null;
            _captureSuspended = false;
        }

        private void CaptureQuad(Texture2D texture, Vector3 color)
        {
            Vector2 uv0 = _quadUVs[0], uv1 = _quadUVs[1], uv2 = _quadUVs[2];
            _capture.AddSprite(texture, _quadPoints[0], _quadPoints[1], _quadPoints[2], _quadPoints[3],
                new Vector4(uv0.X, uv0.Y, uv1.X - uv0.X, uv2.Y - uv0.Y), false, color,
                CaptureDepthAbs, CaptureTieAbs, CaptureS);
        }
        // END PORT DEVIATION (GUO)

        public void SetWorldOffset(int offsetX, int offsetY)
        {
            _worldOffset = new Vector2(-offsetX, -offsetY);

            Cut(CutReason.Offset);
        }

        public void ResetWorldOffset()
        {
            _worldOffset = Vector2.Zero;

            Cut(CutReason.Offset);
        }

        /// <summary>
        /// Changes how subsequent sprites combine with what is underneath.
        /// Passing null restores premultiplied alpha, which is upstream's
        /// default and what <c>BlendState.AlphaBlend</c> means.
        /// </summary>
        /// <remarks>
        /// Godot's canvas has five fixed blend modes and ClassicUO's effects
        /// need equations outside them, so anything but the default switches
        /// to a shader that reads the destination back and evaluates XNA's
        /// blend equation itself. See ADR-0003. That read needs whatever is
        /// underneath copied to the back buffer first, which is why this cuts
        /// two items rather than one: a copier, then the drawing item.
        /// </remarks>
        public void SetBlendState(BlendState blend)
        {
            blend = blend ?? BlendState.AlphaBlend;
#if GUO_LABS
            if (!blend.IsPremultipliedAlpha) _orderedWorld?.Reject("custom blend");
#endif

            // PORT DEVIATION (GUO): --gpu-world draws custom blends on the
            // canvas, after the composite (see BeginCapture).
            _captureSuspended = _capture != null && !blend.IsPremultipliedAlpha;

            if (blend.IsPremultipliedAlpha)
            {
                _currentMaterial = _nextMaterial = _material;
                Cut(CutReason.Material);

                return;
            }

            _currentMaterial = _nextMaterial = GetBlendMaterial(blend);

            // Additive is one of Godot's own blend modes, so the hardware does
            // it and nothing is read back. See uo_hue_add.gdshader for why the
            // read-back is not just slower here but wrong.
            if (blend.IsAdditive)
            {
                Cut(CutReason.Material);

                return;
            }

            // The copy has to be its own item, submitted between what is
            // already drawn and what is about to read it. An empty rect means
            // the whole visible area.
            Rid copier = NewItem(CurrentParent, Transform2D.Identity);
            ItemSetCopyToBackbuffer(copier, true);

            Cut(CutReason.Material);
        }

        private ShaderMaterial GetBlendMaterial(BlendState blend)
        {
            string key = $"{(int)blend.ColorSourceBlend}.{(int)blend.ColorDestinationBlend}"
                + $".{(int)blend.ColorBlendFunction}.{(int)blend.AlphaSourceBlend}"
                + $".{(int)blend.AlphaDestinationBlend}.{(int)blend.AlphaBlendFunction}";

            if (_blendMaterials.TryGetValue(key, out ShaderMaterial cached))
            {
                return cached;
            }

            Reject(blend.ColorSourceBlend);
            Reject(blend.ColorDestinationBlend);
            Reject(blend.AlphaSourceBlend);
            Reject(blend.AlphaDestinationBlend);

            ShaderMaterial material;

            if (blend.IsAdditive)
            {
                _addShader ??= GD.Load<Shader>(ADD_SHADER_PATH);
                material = new ShaderMaterial { Shader = _addShader };
            }
            else
            {
                _blendShader ??= GD.Load<Shader>(BLEND_SHADER_PATH);
                material = new ShaderMaterial { Shader = _blendShader };

                material.SetShaderParameter("color_source_blend", (int)blend.ColorSourceBlend);
                material.SetShaderParameter("color_destination_blend", (int)blend.ColorDestinationBlend);
                material.SetShaderParameter("color_blend_function", (int)blend.ColorBlendFunction);
                material.SetShaderParameter("alpha_source_blend", (int)blend.AlphaSourceBlend);
                material.SetShaderParameter("alpha_destination_blend", (int)blend.AlphaDestinationBlend);
                material.SetShaderParameter("alpha_blend_function", (int)blend.AlphaBlendFunction);
            }

            // The hue tables are global state living on the plain material, and
            // a blend material draws the same sprites, so it needs them too.
            material.SetShaderParameter("hue_texture", _material.GetShaderParameter("hue_texture"));
            material.SetShaderParameter("light_texture", _material.GetShaderParameter("light_texture"));

            // PORT DEVIATION (GUO): the water uniforms are global state too (ADR-0037).
            foreach (KeyValuePair<string, Variant> w in _waterUniforms)
            {
                material.SetShaderParameter(w.Key, w.Value);
            }

            _blendMaterials[key] = material;

            return material;
        }

        private static void Reject(Blend factor)
        {
            // A constant blend colour, and saturation, would need plumbing that
            // has no caller. Throwing names the gap; mapping them to something
            // nearby would not.
            if (factor == Blend.BlendFactor
                || factor == Blend.InverseBlendFactor
                || factor == Blend.SourceAlphaSaturation)
            {
                throw new NotSupportedException(
                    $"Blend.{factor} is not implemented; no ClassicUO blend state uses it. "
                    + "See ADR-0003.");
            }
        }

        /// <summary>
        /// Changes how subsequent sprites are sampled. Passing null restores
        /// nearest neighbour, which is the default and what project rule 7
        /// requires of anything drawing pixel art.
        /// </summary>
        public void SetSampler(SamplerState sampler)
        {
#if GUO_LABS
            if ((sampler ?? SamplerState.PointClamp) != SamplerState.PointClamp)
                _orderedWorld?.Reject("non-nearest sampler");
#endif
            if (TraceSamplerCalls) SamplerCalls++;
            _hdSpriteSampler = false;
            _sampler = sampler ?? SamplerState.PointClamp;

            Cut(CutReason.Sampler);
        }

        // Diagnostic only: the existing perf dump pins the measurement window.
        private static readonly bool TraceSamplerCalls = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GUO_PERF_DUMP"));
        public static long SamplerCalls { get; private set; }

        // HD-only runs keep their sampler until a native sprite or mesh arrives.
        // This cuts only at filter changes, without changing submission order.
        // Explicit upstream SetSampler barriers retain their original behavior.
        private void SelectSpriteSampler(Texture2D texture)
        {
            bool hd = Configuration.ProfileManager.CurrentProfile?.EnhancedHD == true && Arts.Art.IsHdTexture(texture);
            if (!hd && !_hdSpriteSampler) return;
            SamplerState wanted = hd ? Arts.Art.HdSampler(texture) : SamplerState.PointClamp;
            if (_sampler != wanted) SetSampler(wanted);
            _hdSpriteSampler = hd;
        }

        /// <summary>Scoped UI transform; clip children inherit it. Pop with ClipEnd.</summary>
        public void PushUiTransform(Transform2D transform)
        {
#if GUO_LABS
            _orderedWorld?.Reject("nested transform");
#endif
            Rid parent = NewItem(CurrentParent, transform);
            _clipStack.Add(parent);
            Cut(CutReason.Offset);
        }

        public bool ClipBegin(int x, int y, int width, int height)
        {
#if GUO_LABS
            _orderedWorld?.Reject("clip");
#endif
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            // Upstream runs the rect through ScissorStack.CalculateScissors to
            // get it into screen space, and intersects it with the enclosing
            // scissor by hand. Neither is needed here: these items already sit
            // under the node carrying the camera transform, and nesting the
            // clip item inside the enclosing one is what intersects them.
            Rid clip = NewItem(CurrentParent, Transform2D.Identity);

            // PORT DEVIATION (GUO): this is an empty clip ancestor. Finish
            // removing any retained sprite suffix BEFORE enabling clipping:
            // Godot's CanvasItemClear also resets the native clip flag.
            FinishCanvasCommands();
            ItemSetCustomRect(clip, new Rect2(x, y, width, height));
            ItemSetClip(clip, true);

            _clipStack.Add(clip);

            Cut(CutReason.Clip);

            return true;
        }

        public void ClipEnd()
        {
            if (_clipStack.Count > 0)
            {
                _clipStack.RemoveAt(_clipStack.Count - 1);
            }

            Cut(CutReason.Clip);
        }


        public void DrawString(SpriteFont spriteFont, ReadOnlySpan<char> text, int x, int y, Vector3 color, float layerDepth)
            => DrawString(spriteFont, text, new Vector2(x, y), color, layerDepth);

        /// <remarks>
        ///     PORT DEVIATION (GUO): upstream opens with EnsureSize(), which grows
        ///     its CPU vertex array before writing into it. This batcher records
        ///     into a Godot canvas item and owns no such array, so there is nothing
        ///     to size. Everything below is the layout arithmetic, unchanged --
        ///     including the character-map lookups by IndexOf and the commented-out
        ///     throw for an unresolvable character.
        /// </remarks>
        public void DrawString(SpriteFont spriteFont, ReadOnlySpan<char> text, Vector2 position, Vector3 color, float layerDepth)
        {
            if (text.IsEmpty)
            {
                return;
            }

            Texture2D textureValue = spriteFont.Texture;
            List<Rectangle> glyphData = spriteFont.GlyphData;
            List<Rectangle> croppingData = spriteFont.CroppingData;
            List<Vector3> kerning = spriteFont.Kerning;
            List<char> characterMap = spriteFont.CharacterMap;

            Vector2 curOffset = Vector2.Zero;
            bool firstInLine = true;

            Vector2 baseOffset = Vector2.Zero;
            float axisDirX = 1;
            float axisDirY = 1;

            foreach (char c in text)
            {
                // Special characters
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n')
                {
                    curOffset.X = 0.0f;
                    curOffset.Y += spriteFont.LineSpacing;
                    firstInLine = true;

                    continue;
                }

                /* Get the List index from the character map, defaulting to the
				 * DefaultCharacter if it's set.
				 */
                int index = characterMap.IndexOf(c);

                if (index == -1)
                {
                    if (!spriteFont.DefaultCharacter.HasValue)
                    {
                        index = characterMap.IndexOf('?');
                        //throw new ArgumentException(
                        //                            "Text contains characters that cannot be" +
                        //                            " resolved by this SpriteFont.",
                        //                            "text"
                        //                           );
                    }
                    else
                    {
                        index = characterMap.IndexOf(spriteFont.DefaultCharacter.Value);
                    }
                }

                /* For the first character in a line, always push the width
				 * rightward, even if the kerning pushes the character to the
				 * left.
				 */
                Vector3 cKern = kerning[index];

                if (firstInLine)
                {
                    curOffset.X += Math.Abs(cKern.X);
                    firstInLine = false;
                }
                else
                {
                    curOffset.X += spriteFont.Spacing + cKern.X;
                }

                // Calculate the character origin
                Rectangle cCrop = croppingData[index];
                Rectangle cGlyph = glyphData[index];

                float offsetX = baseOffset.X + (curOffset.X + cCrop.X) * axisDirX;
                float offsetY = baseOffset.Y + (curOffset.Y + cCrop.Y) * axisDirY;

                var pos = new Vector2(offsetX, offsetY);
                Draw
                (
                    textureValue,
                    position + pos,
                    cGlyph,
                    color,
                    layerDepth
                );

                curOffset.X += cKern.Y + cKern.Z;
            }
        }


        // ==========================
        // === UO drawing methods ===
        // ==========================

        public struct YOffsets
        {
            public int Top;
            public int Right;
            public int Left;
            public int Bottom;
        }

        public void DrawStretchedLand
        (
            Texture2D texture,
            Vector2 position,
            Rectangle sourceRect,
            ref YOffsets yOffsets,
            ref Vector3 normalTop,
            ref Vector3 normalRight,
            ref Vector3 normalLeft,
            ref Vector3 normalBottom,
            Vector3 hue,
            float depth
        )
        {
            // The four normals are per-vertex lighting input for the shader's
            // LAND modes. A canvas quad's only per-vertex channel is the
            // modulate colour, and ADR-0002 spends all four of its bytes on
            // the hue. Stretched land belongs to the world mesh, where a real
            // vertex format is available.
            throw new NotSupportedException(
                "DrawStretchedLand needs per-vertex normals, which a Godot canvas quad " +
                "cannot carry. It belongs to the world mesh path; see ADR-0002.");
        }

        public void DrawShadow(Texture2D texture, Vector2 position, Rectangle sourceRect, bool flip, float depth)
        {
            float width = sourceRect.Width;
            float height = sourceRect.Height * 0.5f;
            float translatedY = position.Y + height - 10;
            float ratio = height / width;

            _quadPoints[0] = new Vector2(position.X + width * ratio, translatedY);
            _quadPoints[1] = new Vector2(position.X + width * (ratio + 1f), translatedY);
            _quadPoints[2] = new Vector2(position.X, translatedY + height);
            _quadPoints[3] = new Vector2(position.X + width, translatedY + height);

            CalculateHalfPixelUVs(sourceRect, WidthOf(texture), HeightOf(texture),
                out float sourceX, out float sourceY, out float sourceW, out float sourceH);

            byte effects = (byte)((flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None) & (SpriteEffects)0x03);

            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);

            // Hue index 0, SHADER_SHADOW, fully opaque -- upstream's constant.
            Vector3 hue;
            hue.X = 0;
            hue.Y = ShaderHueTranslator.SHADER_SHADOW;
            hue.Z = 1f;

            _idSkip = true; // PORT DEVIATION (GUO): a shadow is not the object (ADR-0023)
            AddQuad(texture, hue);
            _idSkip = false;
        }

        public void DrawCharacterSitted
        (
            Texture2D texture,
            Vector2 position,
            Rectangle sourceRect,
            Vector3 mod,
            Vector3 hue,
            bool flip,
            float depth
        )
        {
            float h03 = sourceRect.Height * mod.X;
            float h06 = sourceRect.Height * mod.Y;
            float h09 = sourceRect.Height * mod.Z;

            float sittingOffset = flip ? -8.0f : 8.0f;

            float width = sourceRect.Width;
            float widthOffset = sourceRect.Width + sittingOffset;

            if (mod.X != 0.0f)
            {
                DrawSittedSection(
                    texture, ref sourceRect, ref hue, flip, depth, mod.X,
                    position.X + sittingOffset, position.Y,
                    position.X + widthOffset, position.Y,
                    position.X + sittingOffset, position.Y + h03,
                    position.X + widthOffset, position.Y + h03,
                    0f);
            }

            if (mod.Y != 0.0f)
            {
                DrawSittedSection(
                    texture, ref sourceRect, ref hue, flip, depth, mod.Y,
                    position.X + sittingOffset, position.Y + h03,
                    position.X + widthOffset, position.Y + h03,
                    position.X, position.Y + h06,
                    position.X + width, position.Y + h06,
                    h03);
            }

            if (mod.Z != 0.0f)
            {
                DrawSittedSection(
                    texture, ref sourceRect, ref hue, flip, depth, mod.Z,
                    position.X, position.Y + h06,
                    position.X + width, position.Y + h06,
                    position.X, position.Y + h09,
                    position.X + width, position.Y + h09,
                    h06);
            }
        }

        private void DrawSittedSection(
            Texture2D texture,
            ref Rectangle sourceRect,
            ref Vector3 hue,
            bool flip,
            float depth,
            float modValue,
            float x0, float y0,
            float x1, float y1,
            float x2, float y2,
            float x3, float y3,
            float uvYOffset)
        {
            _quadPoints[0] = new Vector2(x0, y0);
            _quadPoints[1] = new Vector2(x1, y1);
            _quadPoints[2] = new Vector2(x2, y2);
            _quadPoints[3] = new Vector2(x3, y3);

            CalculateHalfPixelUVs(sourceRect, WidthOf(texture), HeightOf(texture),
                out float sourceX, out float sourceY, out float sourceW, out float sourceH);

            float invH = 1f / HeightOf(texture);
            sourceY += uvYOffset * invH;
            sourceH -= uvYOffset * invH;

            byte effects = (byte)((flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None) & (SpriteEffects)0x03);

            // The bottom two corners take a shortened V: upstream scales their
            // sourceH by modValue and leaves the top two alone.
            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH * modValue, effects);

            AddQuad(texture, hue);
        }

        public void DrawTiled
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Rectangle sourceRectangle,
            Vector3 hue,
            float layerDepth
        )
        {
            int h = destinationRectangle.Height;

            Rectangle rect = sourceRectangle;
            Vector2 pos = new Vector2(destinationRectangle.X, destinationRectangle.Y);

            while (h > 0)
            {
                pos.X = destinationRectangle.X;
                int w = destinationRectangle.Width;

                rect.Height = Math.Min(h, sourceRectangle.Height);

                while (w > 0)
                {
                    rect.Width = Math.Min(w, sourceRectangle.Width);

                    Draw
                    (
                        texture,
                        pos,
                        rect,
                        hue,
                        layerDepth
                    );

                    w -= sourceRectangle.Width;
                    pos.X += sourceRectangle.Width;
                }

                h -= sourceRectangle.Height;
                pos.Y += sourceRectangle.Height;
            }
        }

        public bool DrawRectangle
        (
            Texture2D texture,
            int x,
            int y,
            int width,
            int height,
            Vector3 hue,
            float depth
        )
        {
            Rectangle rect = new Rectangle(x, y, width, 1);
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            rect.X += width;
            rect.Width = 1;
            rect.Height += height;
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            rect.X = x;
            rect.Y = y + height;
            rect.Width = width;
            rect.Height = 1;
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            rect.X = x;
            rect.Y = y;
            rect.Width = 1;
            rect.Height = height;
            Draw(texture, rect, null, hue, 0f, Vector2.Zero, SpriteEffects.None, depth);

            return true;
        }

        public void DrawLine
        (
            Texture2D texture,
            Vector2 start,
            Vector2 end,
            Vector3 color,
            float stroke,
            float depth
        )
        {
            var radians = GUO.Utility.MathHelper.AngleBetweenVectors(start, end);

            // Upstream calls Vector2.Distance(ref, ref, out); Godot's Vector2
            // spells the same thing as an instance method.
            float length = start.DistanceTo(end);

            Draw
            (
                texture,
                start,
                new Rectangle(0, 0, WidthOf(texture), HeightOf(texture)),
                color,
                radians,
                Vector2.Zero,
                new Vector2(length, stroke),
                SpriteEffects.None,
                depth
            );
        }


        // ==========================
        // === Draw overloads =======
        // ==========================

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Vector3 color,
            float depth
        )
        {
            AddSprite(texture, 0f, 0f, 1f, 1f, position.X, position.Y, WidthOf(texture), HeightOf(texture), color, 0f, 0f, 0f, 1f, depth, 0);
        }

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Rectangle? sourceRectangle,
            Vector3 color,
            float depth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;
            float destW, destH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVs(sourceRectangle.Value, WidthOf(texture), HeightOf(texture),
                    out sourceX, out sourceY, out sourceW, out sourceH);
                destW = sourceRectangle.Value.Width;
                destH = sourceRectangle.Value.Height;
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
                destW = WidthOf(texture);
                destH = HeightOf(texture);
            }

            AddSprite(texture, sourceX, sourceY, sourceW, sourceH, position.X, position.Y, destW, destH, color, 0.0f, 0.0f, 0.0f, 1.0f, depth, 0);
        }

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Rectangle? sourceRectangle,
            Vector3 color,
            float rotation,
            Vector2 origin,
            float scale,
            SpriteEffects effects,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;
            float destW = scale;
            float destH = scale;

            if (sourceRectangle.HasValue)
            {
                CalculateUVsSafe(sourceRectangle.Value, WidthOf(texture), HeightOf(texture),
                    out sourceX, out sourceY, out sourceW, out sourceH);
                destW *= sourceRectangle.Value.Width;
                destH *= sourceRectangle.Value.Height;
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
                destW *= WidthOf(texture);
                destH *= HeightOf(texture);
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                position.X,
                position.Y,
                destW,
                destH,
                color,
                origin.X / sourceW / (float)WidthOf(texture),
                origin.Y / sourceH / (float)HeightOf(texture),
                (float)Math.Sin(rotation),
                (float)Math.Cos(rotation),
                layerDepth,
                (byte)(effects & (SpriteEffects)0x03)
            );
        }

        public void Draw
        (
            Texture2D texture,
            Vector2 position,
            Rectangle? sourceRectangle,
            Vector3 color,
            float rotation,
            Vector2 origin,
            Vector2 scale,
            SpriteEffects effects,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVsSafe(sourceRectangle.Value, WidthOf(texture), HeightOf(texture),
                    out sourceX, out sourceY, out sourceW, out sourceH);
                scale.X *= sourceRectangle.Value.Width;
                scale.Y *= sourceRectangle.Value.Height;
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
                scale.X *= WidthOf(texture);
                scale.Y *= HeightOf(texture);
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                position.X,
                position.Y,
                scale.X,
                scale.Y,
                color,
                origin.X / sourceW / (float)WidthOf(texture),
                origin.Y / sourceH / (float)HeightOf(texture),
                (float)Math.Sin(rotation),
                (float)Math.Cos(rotation),
                layerDepth,
                (byte)(effects & (SpriteEffects)0x03)
            );
        }

        public void Draw
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Vector3 color,
            float layerDepth
        )
        {
            AddSprite(
                texture,
                0.0f,
                0.0f,
                1.0f,
                1.0f,
                destinationRectangle.X,
                destinationRectangle.Y,
                destinationRectangle.Width,
                destinationRectangle.Height,
                color,
                0.0f,
                0.0f,
                0.0f,
                1.0f,
                layerDepth,
                0
            );
        }

        public void Draw
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Rectangle? sourceRectangle,
            Vector3 color,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVs(sourceRectangle.Value, WidthOf(texture), HeightOf(texture),
                    out sourceX, out sourceY, out sourceW, out sourceH);
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                destinationRectangle.X,
                destinationRectangle.Y,
                destinationRectangle.Width,
                destinationRectangle.Height,
                color,
                0.0f,
                0.0f,
                0.0f,
                1.0f,
                layerDepth,
                0
            );
        }

        public void Draw
        (
            Texture2D texture,
            Rectangle destinationRectangle,
            Rectangle? sourceRectangle,
            Vector3 color,
            float rotation,
            Vector2 origin,
            SpriteEffects effects,
            float layerDepth
        )
        {
            float sourceX, sourceY, sourceW, sourceH;

            if (sourceRectangle.HasValue)
            {
                CalculateUVsSafe(sourceRectangle.Value, WidthOf(texture), HeightOf(texture),
                    out sourceX, out sourceY, out sourceW, out sourceH);
            }
            else
            {
                sourceX = 0.0f;
                sourceY = 0.0f;
                sourceW = 1.0f;
                sourceH = 1.0f;
            }

            AddSprite
            (
                texture,
                sourceX,
                sourceY,
                sourceW,
                sourceH,
                destinationRectangle.X,
                destinationRectangle.Y,
                destinationRectangle.Width,
                destinationRectangle.Height,
                color,
                origin.X / sourceW / (float)WidthOf(texture),
                origin.Y / sourceH / (float)HeightOf(texture),
                (float)Math.Sin(rotation),
                (float)Math.Cos(rotation),
                layerDepth,
                (byte)(effects & (SpriteEffects)0x03)
            );
        }


        // The last texture measured, and what it measured.
        private Texture2D _sizedTexture;
        private int _sizedWidth, _sizedHeight;
        private Rid _sizedRid;

        /// <summary>
        /// A texture's width, height and RID, asked of the engine once per run
        /// of sprites that share the texture.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream reads Width and Height off an FNA
        /// texture, a C# field. On Godot each is a call into the engine, as is
        /// the RID, and a sprite asked for them five to seven times between the
        /// Draw overload and AddSprite -- some hundred thousand calls a frame
        /// zoomed out (parity night 2026-09-26, P1). World sprites come from a
        /// few atlas pages, so consecutive sprites nearly always share one.
        /// Forgotten at every Begin and BeginFrame; within a batch a texture's
        /// size does not change (ImageTexture.Update keeps it, and render
        /// targets resize between frames).
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Measure(Texture2D texture)
        {
            if (!ReferenceEquals(texture, _sizedTexture))
            {
                TextureSwitches++;
                _sizedTexture = texture;
                if (TextureAtlas.TryGetMetadata(texture, out var page))
                {
                    _sizedWidth = page.Width;
                    _sizedHeight = page.Height;
                    _sizedRid = page.Rid;
                }
                else
                {
                    NativeTextureMeasurements++;
                    _sizedWidth = texture.GetWidth();
                    _sizedHeight = texture.GetHeight();
                    _sizedRid = texture.GetRid();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int WidthOf(Texture2D texture)
        {
            Measure(texture);

            return _sizedWidth;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int HeightOf(Texture2D texture)
        {
            Measure(texture);

            return _sizedHeight;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Rid RidOf(Texture2D texture)
        {
            Measure(texture);

            return _sizedRid;
        }

        // ==========================
        // === The one choke point ==
        // ==========================

        private void AddSprite
        (
            Texture2D texture,
            float sourceX,
            float sourceY,
            float sourceW,
            float sourceH,
            float destinationX,
            float destinationY,
            float destinationW,
            float destinationH,
            Vector3 color,
            float originX,
            float originY,
            float rotationSin,
            float rotationCos,
            float depth,
            byte effects
        )
        {
            EnsureStarted();

            if (texture == null)
            {
                return;
            }

            int textureWidth = WidthOf(texture);
            int textureHeight = HeightOf(texture);
#if GUO_LABS
            if (OrderedWorldRecording && rotationSin == 0f && rotationCos == 1f && effects == 0)
            {
                SetQuadPoints(destinationX, destinationY, destinationW, destinationH, originX, originY, rotationSin, rotationCos);
                SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);
                if (CaptureOrderedQuad(texture, color, true,
                    new Rect2(destinationX - originX * destinationW, destinationY - originY * destinationH, destinationW, destinationH),
                    new Rect2(sourceX * textureWidth, sourceY * textureHeight, sourceW * textureWidth, sourceH * textureHeight))) return;
            }
#endif

            // PORT DEVIATION (GUO): --gpu-world captures the sprite as an
            // instance: the rect path's corners and texel source rect, or the
            // rotated path's corners and normalised UVs, as the canvas would
            // have had them.
            if (_capture != null && !_captureSuspended)
            {
                if (rotationSin == 0f && rotationCos == 1f && effects == 0)
                {
                    float x0 = destinationX - originX * destinationW, y0 = destinationY - originY * destinationH;
                    float x1 = x0 + destinationW, y1 = y0 + destinationH;
                    _capture.AddSprite(texture, new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x0, y1), new Vector2(x1, y1),
                        new Vector4(sourceX * textureWidth, sourceY * textureHeight, sourceW * textureWidth, sourceH * textureHeight),
                        true, color, CaptureDepthAbs, CaptureTieAbs, CaptureS);
                    return;
                }

                SetQuadPoints(destinationX, destinationY, destinationW, destinationH, originX, originY, rotationSin, rotationCos);
                SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);
                CaptureQuad(texture, color);
                return;
            }

#if GUO_LABS
            if (_orderedWorld?.Deferring != true)
#endif
            {
                SelectSpriteSampler(texture);
                EnsureMaterial(_currentMaterial);
            }

            // PORT DEVIATION (GUO): the id mirror (ADR-0023) follows the plain path only.
            if (rotationSin == 0f && rotationCos == 1f && effects == 0 && BatchedWorld && !_mirroring)
            {
                float x0 = destinationX - originX * destinationW, y0 = destinationY - originY * destinationH;
                float x1 = x0 + destinationW, y1 = y0 + destinationH;
                AppendToRun(texture, new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x0, y1), new Vector2(x1, y1),
                    new Vector2(sourceX, sourceY), new Vector2(sourceX + sourceW, sourceY),
                    new Vector2(sourceX, sourceY + sourceH), new Vector2(sourceX + sourceW, sourceY + sourceH), Encode(color));
                return;
            }

            if (rotationSin == 0f && rotationCos == 1f && effects == 0)
            {
                // The overwhelmingly common case: an upright, unmirrored rect.
                // One canvas command, no per-draw arrays.
                FlushRun();
                Commands++;
                Count(0, RidOf(texture));
                var destinationRect = new Rect2(
                    destinationX - originX * destinationW,
                    destinationY - originY * destinationH,
                    destinationW,
                    destinationH);
                var sourceRect = new Rect2(
                    sourceX * textureWidth,
                    sourceY * textureHeight,
                    sourceW * textureWidth,
                    sourceH * textureHeight);
                CanvasRect
                (
                    _current,
                    destinationRect,
                    RidOf(texture),
                    sourceRect,
                    Encode(color)
                );
                MirrorRect(destinationRect, texture, sourceRect); // PORT DEVIATION (GUO): ADR-0023

                return;
            }

            // Rotated or mirrored. The corner maths is upstream's SetVertex,
            // component for component, so a rotated sprite lands on the same
            // pixels it does there.
            SetQuadPoints(destinationX, destinationY, destinationW, destinationH, originX, originY, rotationSin, rotationCos);
            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);

            AddQuad(texture, color);
        }

        /// <summary>Upstream's SetVertex for the four corners of a rotated or mirrored sprite.</summary>
        private void SetQuadPoints(float destinationX, float destinationY, float destinationW, float destinationH,
            float originX, float originY, float rotationSin, float rotationCos)
        {
            float cornerX = -originX * destinationW;
            float cornerY = -originY * destinationH;
            _quadPoints[0] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            cornerX = (1.0f - originX) * destinationW;
            cornerY = -originY * destinationH;
            _quadPoints[1] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            cornerX = -originX * destinationW;
            cornerY = (1.0f - originY) * destinationH;
            _quadPoints[2] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);

            cornerX = (1.0f - originX) * destinationW;
            cornerY = (1.0f - originY) * destinationH;
            _quadPoints[3] = new Vector2(
                (-rotationSin * cornerY) + (rotationCos * cornerX) + destinationX,
                (rotationCos * cornerY) + (rotationSin * cornerX) + destinationY);
        }

        /// <summary>
        /// Fills the four UVs the way upstream does, by indexing the corner
        /// offset tables with <c>i ^ effects</c> — which is how a mirrored
        /// sprite gets its corners swapped rather than its rect negated.
        /// </summary>
        /// <param name="sourceHBottom">
        /// The V extent the bottom two corners use. Equal to
        /// <paramref name="sourceH"/> for everything except a sitted
        /// character, whose lower corners take a shortened slice.
        /// </param>
        private void SetQuadUVs(float sourceX, float sourceY, float sourceW, float sourceH, float sourceHBottom, byte effects)
        {
            _quadUVs[0] = new Vector2(
                (_cornerOffsetX[0 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[0 ^ effects] * sourceH) + sourceY);
            _quadUVs[1] = new Vector2(
                (_cornerOffsetX[1 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[1 ^ effects] * sourceH) + sourceY);
            _quadUVs[2] = new Vector2(
                (_cornerOffsetX[2 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[2 ^ effects] * sourceHBottom) + sourceY);
            _quadUVs[3] = new Vector2(
                (_cornerOffsetX[3 ^ effects] * sourceW) + sourceX,
                (_cornerOffsetY[3 ^ effects] * sourceHBottom) + sourceY);
        }

        /// <summary>
        /// Submits <see cref="_quadPoints"/> and <see cref="_quadUVs"/> as two
        /// triangles. Used for anything a rect command cannot express: a
        /// rotation, a mirror, a shadow's parallelogram, a sitted section.
        /// </summary>
        private void AddQuad(Texture2D texture, Vector3 color)
        {
            EnsureStarted();

            if (texture == null)
            {
                return;
            }

            // PORT DEVIATION (GUO): --gpu-world captures the quad (a shadow's
            // parallelogram, a mirror, a sitted section) as an instance.
            if (_capture != null && !_captureSuspended)
            {
                CaptureQuad(texture, color);
                return;
            }

#if GUO_LABS
            if (_orderedWorld?.Deferring != true)
#endif
            {
                SelectSpriteSampler(texture);
                EnsureMaterial(_currentMaterial);
            }

            Color modulate = Encode(color);
            MirrorQuad(texture); // PORT DEVIATION (GUO): ADR-0023's id mirror, before any path returns

            if (BatchedWorld && !_mirroring)
            {
#if GUO_LABS
                if (CaptureOrderedQuad(texture, color)) return;
#endif
                AppendToRun(texture, _quadPoints[0], _quadPoints[1], _quadPoints[2], _quadPoints[3],
                    _quadUVs[0], _quadUVs[1], _quadUVs[2], _quadUVs[3], modulate);
                return;
            }

            if (TryAddAffineQuad(texture, modulate, color))
            {
                return;
            }

            _quadColors[0] = modulate;
#if GUO_LABS
            if (CaptureOrderedQuad(texture, color)) return;
            // A rejection emitted the captured prefix; prepare this original suffix draw.
            if (OrderedWorldRecording)
            {
                SelectSpriteSampler(texture);
                EnsureMaterial(_currentMaterial);
            }
#endif
            _quadColors[1] = modulate;
            _quadColors[2] = modulate;
            _quadColors[3] = modulate;
            FlushRun();

            Commands++;
            Count(3, RidOf(texture));

            _canvasCommands?.Bypass();
            RenderingServer.CanvasItemAddTriangleArray
            (
                _current,
                _quadIndices,
                _quadPoints,
                _quadColors,
                _quadUVs,
                null,
                null,
                RidOf(texture)
            );
        }

        /// <summary>
        /// Draws the quad as one texture rect under a transform, when it is
        /// the image of its texture region under an affine map -- which a
        /// shadow's parallelogram, a mirror, a rotation and a sitted section
        /// all are.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO), for speed alone: the picture is the same two
        /// triangles with the same UVs. A triangle array hands the engine four
        /// managed arrays, each copied into a packed array and freed again, per
        /// quad; with the static shadows of a wide view that was a sixth of the
        /// frame (parity night 2026-09-26, P1). The map is solved from three
        /// corners in texel space, so the rect's own coordinates are the
        /// region's; the fourth corner has to land where the quad puts it and
        /// the UVs have to span an axis-aligned region, or the quad goes the
        /// old way.
        /// </remarks>
        private bool TryAddAffineQuad(Texture2D texture, Color modulate, Vector3 hue)
        {
            var size = new Vector2(WidthOf(texture), HeightOf(texture));

            Vector2 t0 = _quadUVs[0] * size;
            Vector2 t1 = _quadUVs[1] * size;
            Vector2 t2 = _quadUVs[2] * size;
            Vector2 t3 = _quadUVs[3] * size;

            Vector2 m0 = t1 - t0;
            Vector2 m1 = t2 - t0;
            float det = m0.X * m1.Y - m1.X * m0.Y;

            if (Math.Abs(det) < 1e-4f)
            {
                return false;
            }

            Vector2 p0 = _quadPoints[0];
            Vector2 q0 = _quadPoints[1] - p0;
            Vector2 q1 = _quadPoints[2] - p0;

            // Columns of Q * inverse(M), M's columns being m0 and m1.
            Vector2 axisX = (q0 * m1.Y - q1 * m0.Y) / det;
            Vector2 axisY = (q1 * m0.X - q0 * m1.X) / det;
            Vector2 origin = p0 - axisX * t0.X - axisY * t0.Y;

            Vector2 fourth = origin + axisX * t3.X + axisY * t3.Y;

            if ((fourth - _quadPoints[3]).LengthSquared() > 1e-4f)
            {
                return false;
            }

            float left = Math.Min(Math.Min(t0.X, t1.X), Math.Min(t2.X, t3.X));
            float right = Math.Max(Math.Max(t0.X, t1.X), Math.Max(t2.X, t3.X));
            float top = Math.Min(Math.Min(t0.Y, t1.Y), Math.Min(t2.Y, t3.Y));
            float bottom = Math.Max(Math.Max(t0.Y, t1.Y), Math.Max(t2.Y, t3.Y));

            if (!OnCorner(t0, left, right, top, bottom) || !OnCorner(t1, left, right, top, bottom)
                || !OnCorner(t2, left, right, top, bottom) || !OnCorner(t3, left, right, top, bottom))
            {
                return false;
            }

            var region = new Rect2(left, top, right - left, bottom - top);
#if GUO_LABS
            if (CaptureOrderedAffine(texture, hue, region, size, new Transform2D(axisX, axisY, origin))) return true;
            if (OrderedWorldRecording)
            {
                SelectSpriteSampler(texture);
                EnsureMaterial(_currentMaterial);
            }
#endif

            FlushRun();
            Commands++;
            Count(1, RidOf(texture));
            CanvasAffine(region, RidOf(texture), modulate, new Transform2D(axisX, axisY, origin));

            return true;
        }

        private static bool OnCorner(Vector2 t, float left, float right, float top, float bottom)
        {
            const float Near = 1e-3f;

            return (Math.Abs(t.X - left) < Near || Math.Abs(t.X - right) < Near)
                && (Math.Abs(t.Y - top) < Near || Math.Abs(t.Y - bottom) < Near);
        }

        /// <summary>
        /// Packs upstream's (hue index, shader mode, alpha) into the four
        /// bytes of a modulate colour. ADR-0002 explains why the index needs
        /// two channels and why the circle-of-transparency flag moved into the
        /// mode byte.
        /// </summary>
        internal static Color Encode(Vector3 color)
        {
            int index = (int)color.X;
            int mode = (int)color.Y;
            float alpha = color.Z;

            // GetHueVector signals circle-of-transparency by adding 1f to the
            // alpha. A colour channel cannot hold that, so it is undone here
            // and carried in the mode byte's top bit -- modes reach 30.
            if (alpha > 1f)
            {
                mode |= 0x80;
                alpha -= 1f;
            }

            return new Color(
                ((index >> 8) & 0xFF) / 255f,
                (index & 0xFF) / 255f,
                (mode & 0xFF) / 255f,
                alpha);
        }


        // ==========================
        // === Canvas item pool =====
        // ==========================

        /// <summary>
        /// Starts a new item if the current one is under a different material.
        /// No-ops when it already is, which is what lets consecutive chunk
        /// meshes share one item.
        /// </summary>
        private void EnsureMaterial(ShaderMaterial material)
        {
            if (ReferenceEquals(_itemMaterial, material))
            {
                return;
            }

            _nextMaterial = material;

            Cut(CutReason.Material);

            _nextMaterial = _currentMaterial;
        }

        private Rid CurrentParent => _clipStack.Count > 0 ? _clipStack[_clipStack.Count - 1] : _target;

        /// <summary>
        /// Ends the current run of commands and starts a fresh item under the
        /// current clip. Called whenever ordering would otherwise be lost.
        /// </summary>
        private void Cut(CutReason reason = CutReason.Other)
        {
            _cuts[(int)reason]++;
            _itemOffset = _worldOffset;
            _current = NewItem(CurrentParent,
                _worldOffset != Vector2.Zero ? new Transform2D(0f, _worldOffset) : Transform2D.Identity);
        }

        /// <summary>The pool index of the item NewItem handed out last; the ItemSet helpers act on it.</summary>
        private int _lastItem = -1;

        private Rid NewItem(Rid parent, in Transform2D transform)
        {
            FlushRun();
            FinishCanvasCommands();
            Rid item;
            int index = _itemCount;

            if (index < _items.Count)
            {
                item = _items[index];
            }
            else
            {
                item = RenderingServer.CanvasItemCreate();
                _items.Add(item);
                if (_itemStates.Length < _items.Count)
                {
                    System.Array.Resize(ref _itemStates, _itemStates.Length * 2);
                }

                // Siblings paint in draw-index order, and the pool index only
                // ever goes up within a frame, so a later item paints over an
                // earlier one. The index is the item's for life.
                RenderingServer.CanvasItemSetDrawIndex(item, index);
                _itemStates[index] = new ItemState { Transform = Transform2D.Identity, Filter = NoFilter };
            }

            ref ItemState state = ref _itemStates[index];
            if (state.Parent != parent)
            {
                RenderingServer.CanvasItemSetParent(item, parent);
                state.Parent = parent;
            }

            if (state.Transform != transform)
            {
                RenderingServer.CanvasItemSetTransform(item, transform);
                state.Transform = transform;
            }

            if (state.Clip)
            {
                RenderingServer.CanvasItemSetClip(item, false);
                state.Clip = false;
            }

            if (state.CustomRect)
            {
                RenderingServer.CanvasItemSetCustomRect(item, false);
                state.CustomRect = false;
            }

            // Reset whenever it differs, not only on creation: a pooled item
            // may have last been used under a different blend, or as a
            // back-buffer copier, and either would carry over into this frame.
            if (!ReferenceEquals(state.Material, _nextMaterial))
            {
                RenderingServer.CanvasItemSetMaterial(item, _nextMaterial.GetRid());
                state.Material = _nextMaterial;
            }

            if (state.CopyToBackbuffer)
            {
                RenderingServer.CanvasItemSetCopyToBackbuffer(item, false, new Rect2());
                state.CopyToBackbuffer = false;
            }

            _itemMaterial = _nextMaterial;

            // Never left to the project setting: a canvas item made through
            // RenderingServer does NOT pick up default_texture_filter, it
            // starts on linear. Measured by launchers/dev/batcher_probe.bat,
            // which read brightness 8 back as 7 -- a 0.875/0.125 blend with
            // the dark texel next door. Every sprite in the client was being
            // smeared, and the only visible symptom would have been slightly
            // soft art. Project rule 7. (NoFilter on a new item sends it.)
            if (state.Filter != _sampler.Filter)
            {
                RenderingServer.CanvasItemSetDefaultTextureFilter(item, _sampler.Filter);
                state.Filter = _sampler.Filter;
            }

            _lastItem = index;
            _itemCount++;
            FlushesDone++;
            StartCanvasCommands(index, item);

            return item;
        }

        // The pooled item's state the server holds, kept beside every change
        // so NewItem can skip what is already set. Only the item NewItem gave
        // out last is ever changed after the fact.
        private ref ItemState StateOf(Rid item)
        {
            System.Diagnostics.Debug.Assert(_lastItem >= 0 && _items[_lastItem] == item, "only the newest pooled item is changed after NewItem");
            return ref _itemStates[_lastItem];
        }

        private void ItemSetMaterial(Rid item, Material material)
        {
            ref ItemState state = ref StateOf(item);
            if (ReferenceEquals(state.Material, material)) return;
            RenderingServer.CanvasItemSetMaterial(item, material.GetRid());
            state.Material = material;
        }

        private void ItemSetCopyToBackbuffer(Rid item, bool copy)
        {
            ref ItemState state = ref StateOf(item);
            if (state.CopyToBackbuffer == copy) return;
            RenderingServer.CanvasItemSetCopyToBackbuffer(item, copy, new Rect2());
            state.CopyToBackbuffer = copy;
        }

        private void ItemSetClip(Rid item, bool clip)
        {
            ref ItemState state = ref StateOf(item);
            if (state.Clip == clip) return;
            RenderingServer.CanvasItemSetClip(item, clip);
            state.Clip = clip;
        }

        private void ItemSetCustomRect(Rid item, Rect2 rect)
        {
            ref ItemState state = ref StateOf(item);
            if (state.CustomRect && state.CustomRectValue == rect) return;
            RenderingServer.CanvasItemSetCustomRect(item, true, rect);
            state.CustomRect = true;
            state.CustomRectValue = rect;
        }


        // ==========================
        // === UV helpers ===========
        // ==========================

        private static void CalculateUVsSafe(
            Rectangle source,
            int textureWidth, int textureHeight,
            out float sourceX, out float sourceY,
            out float sourceW, out float sourceH)
        {
            float invW = 1f / textureWidth;
            float invH = 1f / textureHeight;
            sourceX = source.X * invW;
            sourceY = source.Y * invH;
            sourceW = Math.Sign(source.Width) * Math.Max(Math.Abs(source.Width), GUO.Utility.MathHelper.MachineEpsilonFloat) * invW;
            sourceH = Math.Sign(source.Height) * Math.Max(Math.Abs(source.Height), GUO.Utility.MathHelper.MachineEpsilonFloat) * invH;
        }

        private static void CalculateUVs(
            Rectangle source,
            int textureWidth, int textureHeight,
            out float sourceX, out float sourceY,
            out float sourceW, out float sourceH)
        {
            float invW = 1f / textureWidth;
            float invH = 1f / textureHeight;
            sourceX = source.X * invW;
            sourceY = source.Y * invH;
            sourceW = source.Width * invW;
            sourceH = source.Height * invH;
        }

        private static void CalculateHalfPixelUVs(
            Rectangle sourceRect,
            int textureWidth, int textureHeight,
            out float sourceX, out float sourceY,
            out float sourceW, out float sourceH)
        {
            float invW = 1f / textureWidth;
            float invH = 1f / textureHeight;
            sourceX = (sourceRect.X + 0.5f) * invW;
            sourceY = (sourceRect.Y + 0.5f) * invH;
            sourceW = (sourceRect.Width - 1f) * invW;
            sourceH = (sourceRect.Height - 1f) * invH;
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void EnsureStarted()
        {
            if (!_started)
            {
                throw new InvalidOperationException("UltimaBatcher2D: Begin() has not been called.");
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void EnsureNotStarted()
        {
            if (_started)
            {
                throw new InvalidOperationException("UltimaBatcher2D: End() has not been called.");
            }
        }
    }
}
