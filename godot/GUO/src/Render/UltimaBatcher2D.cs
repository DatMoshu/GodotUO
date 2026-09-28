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
    public sealed class UltimaBatcher2D : IDisposable
    {
        private const string SHADER_PATH = "res://src/Render/shaders/uo_hue.gdshader";
        private const string BLEND_SHADER_PATH = "res://src/Render/shaders/uo_hue_blend.gdshader";
        private const string ADD_SHADER_PATH = "res://src/Render/shaders/uo_hue_add.gdshader";
        private const string MESH_SHADER_PATH = "res://src/Render/shaders/uo_hue_mesh.gdshader";

        private static readonly float[] _cornerOffsetX = new float[] { 0.0f, 1.0f, 0.0f, 1.0f };
        private static readonly float[] _cornerOffsetY = new float[] { 0.0f, 0.0f, 1.0f, 1.0f };

        // A quad as two triangles, in upstream's winding (GenerateIndexArray).
        private static readonly int[] _quadIndices = { 0, 1, 2, 1, 3, 2 };

        private readonly Rid _parent;

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

        private readonly List<Rid> _clipStack = new List<Rid>();
        private Rid _current;

        private bool _started;
        private Vector2 _worldOffset;
        private SamplerState _sampler = SamplerState.PointClamp;

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
        public static (int Rects, int Affine, int Meshes, int Triangles, int Batches, int CoverMeshes) LastKinds;
        private int _lastKind = -1;
        private Rid _lastKindItem, _lastKindTexture;

        private void Count(int kind, Rid texture)
        {
            Kinds[kind]++;
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

        /// <summary>Sends the pending run, if there is one, as one triangle array.</summary>
        private void FlushRun()
        {
            if (_runPoints.Count == 0)
            {
                _runTexture = null;
                return;
            }

            Commands++;
            Count(3, RidOf(_runTexture));
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
            for (int i = 0; i < _items.Count; i++)
            {
                RenderingServer.FreeRid(_items[i]);
            }

            _items.Clear();

            foreach (ShaderMaterial material in _blendMaterials.Values)
            {
                material.Dispose();
            }

            _blendMaterials.Clear();
            _meshMaterial?.Dispose();
            _material?.Dispose();
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
        private void SetOnAllMaterials(string name, Variant value)
        {
            _material.SetShaderParameter(name, value);
            _meshMaterial.SetShaderParameter(name, value);

            foreach (ShaderMaterial material in _blendMaterials.Values)
            {
                material.SetShaderParameter(name, value);
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
        public void BeginFrame()
        {
            EnsureNotStarted();

            LastFrame = (TextureSwitches, _itemCount, Commands);
            LastKinds = (Kinds[0], Kinds[1], Kinds[2], Kinds[3], EstimatedBatches, Kinds[4]);
            System.Array.Clear(Kinds);
            EstimatedBatches = 0;
            _lastKind = -1;
            FramesBegun++;
            _itemCount = 0;
            _sizedTexture = null;
            TextureSwitches = 0;
            FlushesDone = 0;
            Commands = 0;

            // Once a frame, before anything draws. Godot cannot upload part of
            // a texture, so TextureAtlas blits sprites into a CPU-side page as
            // they decode and defers the upload to here; see its remarks for
            // what the alternative costs.
            TextureAtlas.FlushAll();

            // Everything in the pool, not just what this frame ends up using:
            // an item left over from a busier frame would otherwise keep
            // painting last frame's sprites.
            for (int i = 0; i < _items.Count; i++)
            {
                RenderingServer.CanvasItemClear(_items[i]);
            }
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

            Cut();
        }

        public void End()
        {
            EnsureStarted();
            FlushRun();

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
                FlushRun();

                Commands++;
                Count(2, default);

                RenderingServer.CanvasItemAddMesh(
                    _current,
                    mesh.GetRid(),
                    Transform2D.Identity,
                    Colors.White,
                    run.Texture.GetRid());
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

            ArrayMesh mesh = layer.GetSpriteMesh(index);

            // The same route DrawMeshLayer takes: the offset on the item, the
            // mesh drawn untransformed. A run of covering tiles shares one item.
            var offset = new Vector2(-offsetX, -offsetY);

            if (!ReferenceEquals(_itemMaterial, _meshMaterial) || _itemOffset != offset)
            {
                Vector2 keep = _worldOffset;

                _worldOffset = offset;
                _nextMaterial = _meshMaterial;
                Cut();
                _nextMaterial = _currentMaterial;
                _worldOffset = keep;
            }
            FlushRun();

            Commands++;
            Count(4, default);

            RenderingServer.CanvasItemAddMesh(
                _current,
                mesh.GetRid(),
                Transform2D.Identity,
                Colors.White,
                layer.Textures[index].GetRid());
            MirrorBackgroundMesh(mesh, offset, layer.Textures[index]); // PORT DEVIATION (GUO): ADR-0023

            return 1;
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

        public void SetWorldOffset(int offsetX, int offsetY)
        {
            _worldOffset = new Vector2(-offsetX, -offsetY);

            Cut();
        }

        public void ResetWorldOffset()
        {
            _worldOffset = Vector2.Zero;

            Cut();
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

            if (blend.IsPremultipliedAlpha)
            {
                _currentMaterial = _nextMaterial = _material;
                Cut();

                return;
            }

            _currentMaterial = _nextMaterial = GetBlendMaterial(blend);

            // Additive is one of Godot's own blend modes, so the hardware does
            // it and nothing is read back. See uo_hue_add.gdshader for why the
            // read-back is not just slower here but wrong.
            if (blend.IsAdditive)
            {
                Cut();

                return;
            }

            // The copy has to be its own item, submitted between what is
            // already drawn and what is about to read it. An empty rect means
            // the whole visible area.
            Rid copier = NewItem(CurrentParent);
            RenderingServer.CanvasItemSetCopyToBackbuffer(copier, true, new Rect2());

            Cut();
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
            _sampler = sampler ?? SamplerState.PointClamp;

            Cut();
        }

        /// <summary>Scoped UI transform; clip children inherit it. Pop with ClipEnd.</summary>
        public void PushUiTransform(Transform2D transform)
        {
            Rid parent = NewItem(CurrentParent);
            RenderingServer.CanvasItemSetTransform(parent, transform);
            _clipStack.Add(parent);
            Cut();
        }

        public bool ClipBegin(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            // Upstream runs the rect through ScissorStack.CalculateScissors to
            // get it into screen space, and intersects it with the enclosing
            // scissor by hand. Neither is needed here: these items already sit
            // under the node carrying the camera transform, and nesting the
            // clip item inside the enclosing one is what intersects them.
            Rid clip = NewItem(CurrentParent);

            RenderingServer.CanvasItemSetCustomRect(clip, true, new Rect2(x, y, width, height));
            RenderingServer.CanvasItemSetClip(clip, true);

            _clipStack.Add(clip);

            Cut();

            return true;
        }

        public void ClipEnd()
        {
            if (_clipStack.Count > 0)
            {
                _clipStack.RemoveAt(_clipStack.Count - 1);
            }

            Cut();
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
                _sizedWidth = texture.GetWidth();
                _sizedHeight = texture.GetHeight();
                _sizedRid = texture.GetRid();
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

            EnsureMaterial(_currentMaterial);

            int textureWidth = WidthOf(texture);
            int textureHeight = HeightOf(texture);

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
                RenderingServer.CanvasItemAddTextureRectRegion
                (
                    _current,
                    destinationRect,
                    RidOf(texture),
                    sourceRect,
                    Encode(color),
                    false,
                    // Upstream clamps nothing, so neither does this.
                    false
                );
                MirrorRect(destinationRect, texture, sourceRect); // PORT DEVIATION (GUO): ADR-0023

                return;
            }

            // Rotated or mirrored. The corner maths is upstream's SetVertex,
            // component for component, so a rotated sprite lands on the same
            // pixels it does there.
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

            SetQuadUVs(sourceX, sourceY, sourceW, sourceH, sourceH, effects);

            AddQuad(texture, color);
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

            EnsureMaterial(_currentMaterial);

            Color modulate = Encode(color);
            MirrorQuad(texture); // PORT DEVIATION (GUO): ADR-0023's id mirror, before any path returns

            if (BatchedWorld && !_mirroring)
            {
                AppendToRun(texture, _quadPoints[0], _quadPoints[1], _quadPoints[2], _quadPoints[3],
                    _quadUVs[0], _quadUVs[1], _quadUVs[2], _quadUVs[3], modulate);
                return;
            }

            if (TryAddAffineQuad(texture, modulate))
            {
                return;
            }

            _quadColors[0] = modulate;
            _quadColors[1] = modulate;
            _quadColors[2] = modulate;
            _quadColors[3] = modulate;
            FlushRun();

            Commands++;
            Count(3, RidOf(texture));

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
        private bool TryAddAffineQuad(Texture2D texture, Color modulate)
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

            FlushRun();
            RenderingServer.CanvasItemAddSetTransform(_current, new Transform2D(axisX, axisY, origin));
            Commands++;
            Count(1, RidOf(texture));
            RenderingServer.CanvasItemAddTextureRectRegion(_current, region, RidOf(texture), region, modulate, false, false);
            RenderingServer.CanvasItemAddSetTransform(_current, Transform2D.Identity);

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

            Cut();

            _nextMaterial = _currentMaterial;
        }

        private Rid CurrentParent => _clipStack.Count > 0 ? _clipStack[_clipStack.Count - 1] : _target;

        /// <summary>
        /// Ends the current run of commands and starts a fresh item under the
        /// current clip. Called whenever ordering would otherwise be lost.
        /// </summary>
        private void Cut()
        {
            _current = NewItem(CurrentParent);
            _itemOffset = _worldOffset;

            if (_worldOffset != Vector2.Zero)
            {
                RenderingServer.CanvasItemSetTransform(
                    _current, new Transform2D(0f, _worldOffset));
            }
        }

        private Rid NewItem(Rid parent)
        {
            FlushRun();
            Rid item;

            if (_itemCount < _items.Count)
            {
                item = _items[_itemCount];
            }
            else
            {
                item = RenderingServer.CanvasItemCreate();
                RenderingServer.CanvasItemSetMaterial(item, _nextMaterial.GetRid());

                _items.Add(item);
            }

            RenderingServer.CanvasItemSetParent(item, parent);
            RenderingServer.CanvasItemSetTransform(item, Transform2D.Identity);
            RenderingServer.CanvasItemSetClip(item, false);
            RenderingServer.CanvasItemSetCustomRect(item, false);

            // Reset every time, not only on creation: a pooled item was very
            // likely last used under a different blend, or as a back-buffer
            // copier, and either would carry over into this frame.
            RenderingServer.CanvasItemSetMaterial(item, _nextMaterial.GetRid());
            RenderingServer.CanvasItemSetCopyToBackbuffer(item, false, new Rect2());

            _itemMaterial = _nextMaterial;

            // Set every time, and never left to the project setting: a canvas
            // item made through RenderingServer does NOT pick up
            // default_texture_filter, it starts on linear. Measured by
            // launchers/dev/batcher_probe.bat, which read brightness 8 back
            // as 7 -- a 0.875/0.125 blend with the dark texel next door. Every
            // sprite in the client was being smeared, and the only visible
            // symptom would have been slightly soft art. Project rule 7.
            RenderingServer.CanvasItemSetDefaultTextureFilter(item, _sampler.Filter);

            // Siblings paint in draw-index order, and the pool index only ever
            // goes up within a frame, so a later item paints over an earlier one.
            RenderingServer.CanvasItemSetDrawIndex(item, _itemCount);

            _itemCount++;
            FlushesDone++;

            return item;
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
