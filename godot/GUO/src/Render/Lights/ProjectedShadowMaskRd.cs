// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: the max-combined floor mask (ADR-0039) drawn on the RenderingDevice.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.Lights;

/// <summary>
/// The transplanted GPU max mask from lighting/perf-20261029 @ bbd51955: stamps, occluders
/// and water exclusions, rasterised by the RenderingDevice into two R8 targets
/// (shadow, occlusion) with MAX blending, so duplicate and crossing casters never
/// stack. The present shader blurs and composes them; nothing is read back.
/// Render-thread only; any device failure fails the frame closed (no shadows)
/// with no CPU rasterizer fallback (ADR-0048).
/// </summary>
internal sealed class ProjectedShadowMaskRd : IDisposable
{
    private const int QuadBytes = 64;
    private const int MaxQuads = 65536;
    private const float AlphaCap = 166f / 255f;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Quad
    {
        public float Ax, Ay, Bx, By;
        public float Cx, Cy, From, To;
        public float Ux, Uy, Vx, Vy;
        public float Wx, Wy, Opacity, Solid;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Push
    {
        public float ScreenX, ScreenY, Pad0, Pad1;
        public uint First, Mode, Pad2, Pad3;
    }

    private const string VertexGlsl = @"#version 450
struct Quad { vec4 ab; vec4 c_from_to; vec4 uv; vec4 w_opacity_solid; };
layout(set = 0, binding = 0, std430) readonly buffer Quads { Quad quads[]; };
layout(push_constant, std430) uniform Frame { vec4 screen; uvec4 draw; } pc;
layout(location = 0) out vec2 uv;
layout(location = 1) out vec3 opacity_t_solid;
void main() {
    uint v = pc.draw.x + uint(gl_VertexIndex);
    Quad q = quads[v / 6u];
    const uint order[6] = uint[](0u, 1u, 2u, 1u, 3u, 2u);
    uint corner = order[v % 6u];
    vec2 a = q.ab.xy, b = q.ab.zw, c = q.c_from_to.xy;
    vec2 u = q.uv.xy, w1 = q.uv.zw, w2 = q.w_opacity_solid.xy;
    vec2 p = corner == 0u ? a : corner == 1u ? b : corner == 2u ? c : b + c - a;
    uv = corner == 0u ? u : corner == 1u ? w1 : corner == 2u ? w2 : w1 + w2 - u;
    float t = corner < 2u ? q.c_from_to.z : q.c_from_to.w;
    opacity_t_solid = vec3(q.w_opacity_solid.z, t, q.w_opacity_solid.w);
    gl_Position = vec4(p * (2.0 / pc.screen.xy) - 1.0, 0.0, 1.0);
}
";

    private const string FragmentGlsl = @"#version 450
layout(set = 1, binding = 0) uniform sampler2D art;
layout(push_constant, std430) uniform Frame { vec4 screen; uvec4 draw; } pc;
layout(location = 0) in vec2 uv;
layout(location = 1) in vec3 opacity_t_solid;
layout(location = 0) out vec4 color;
void main() {
    float alpha = opacity_t_solid.z > 0.5 ? 1.0 : texture(art, uv).a;
    float value = alpha * opacity_t_solid.x;
    if (pc.draw.y == 0u) {
        value *= clamp(1.0 - 0.65 * opacity_t_solid.y, 0.2, 1.0);
        value = min(value, 166.0 / 255.0);
    }
    color = vec4(value, 0.0, 0.0, 1.0);
}
";

    private readonly bool _publish;
    internal Rid ShadowRid => _shadow;
    internal ProjectedShadowMaskRd(RD device = null, bool publish = true) { _rd = device; _publish = publish; }
    private RD _rd;
    private Rid _shader, _pipeline, _sampler, _white, _storage, _storageSet;
    private Rid _shadow, _occlusion, _shadowFramebuffer, _occlusionFramebuffer;
    private uint _storageCapacity;
    private int _width, _height, _frame;
    private readonly Dictionary<Rid, (Rid set, int frame)> _textureSets = new();
    private readonly List<Rid> _expired = new();
    private readonly List<Quad> _stamps = new(1024), _occluders = new(4096);
    private readonly List<Rid> _stampTextures = new(1024), _occluderTextures = new(4096);
    private int[] _order = Array.Empty<int>();
    private ulong[] _orderKeys = Array.Empty<ulong>();
    private byte[] _upload = Array.Empty<byte>();
    private readonly Color[] _clear = { new Color(0f, 0f, 0f, 0f) }; // R8: Colors.Transparent would clear red to 1.
    private readonly Texture2Drd _shadowTexture = new(), _occlusionTexture = new();

    public bool Collecting { get; private set; }
    public bool Supported { get; private set; }
    /// <summary>A device error disables this optional sun mask.</summary>
    public bool Failed { get; private set; }
    public Texture2D OcclusionTexture => _occlusionTexture;
    public Vector2 Texel => _width > 0 ? new Vector2(1f / _width, 1f / _height) : Vector2.Zero;

    /// <summary>A main RenderingDevice on this thread: the RD mask can run.</summary>
    internal static bool Available => RenderingServer.IsOnRenderThread() && RenderingServer.GetRenderingDevice() != null;

    public void Register(Texture2D texture, Image image) { } // The device samples the texture itself.

    public void Begin(int width, int height)
    {
        _frame++;
        _stamps.Clear(); _occluders.Clear(); _stampTextures.Clear(); _occluderTextures.Clear();
        Collecting = Supported = !Failed && width > 0 && height > 0 && (_rd != null || Available);
        if (!Supported) return;
        try { EnsureDevice(width, height); }
        catch (Exception e) { Fail(e); }
    }

    public void End() => Collecting = false;

    // Synthetic fixtures and genuinely opaque geometry only; art uses Stamp.
    internal void StampOpaque(Vector2 a, Vector2 b, Vector2 c, float opacity)
    {
        if (!Collecting || !Supported) return;
        if (_stamps.Count == MaxQuads) { Supported = false; return; }
        _stamps.Add(new Quad { Ax=a.X, Ay=a.Y, Bx=b.X, By=b.Y, Cx=c.X, Cy=c.Y,
            Opacity=Math.Clamp(opacity,0,1), Solid=1 });
        _stampTextures.Add(default);
    }

    public void Stamp(Texture2D texture, Vector2 a, Vector2 b, Vector2 c,
        Vector2 u, Vector2 v, Vector2 w, float opacity, float from, float to)
    {
        if (!Collecting || !Supported || texture == null) return;
        if (_stamps.Count == MaxQuads) { Supported = false; return; }
        _stamps.Add(new Quad { Ax = a.X, Ay = a.Y, Bx = b.X, By = b.Y, Cx = c.X, Cy = c.Y, From = from, To = to,
            Ux = u.X, Uy = u.Y, Vx = v.X, Vy = v.Y, Wx = w.X, Wy = w.Y, Opacity = opacity, Solid = 0f });
        _stampTextures.Add(texture.GetRid());
    }

    public void Occluder(Texture2D texture, Vector2 a, Vector2 b, Vector2 c,
        Vector2 u, Vector2 v, Vector2 w, float opacity)
    {
        if (!Collecting || !Supported || opacity <= 0) return;
        if (_occluders.Count == MaxQuads) { Supported = false; return; }
        _occluders.Add(new Quad { Ax = a.X, Ay = a.Y, Bx = b.X, By = b.Y, Cx = c.X, Cy = c.Y,
            Ux = u.X, Uy = u.Y, Vx = v.X, Vy = v.Y, Wx = w.X, Wy = w.Y, Opacity = opacity, Solid = texture == null ? 1f : 0f });
        _occluderTextures.Add(texture == null ? default : texture.GetRid());
    }

    public void Exclude(Vector2 a, Vector2 b, Vector2 c) =>
        Occluder(null, a, b, c, Vector2.Zero, Vector2.Zero, Vector2.Zero, 1f);

    public Texture2D Finish()
    {
        Collecting = false;
        if (!Supported || _stamps.Count == 0) return null;
        try
        {
            Draw();
            DumpIfAsked();
            return _shadowTexture;
        }
        catch (Exception e) { Fail(e); return null; }
    }

    /// <summary>GUO_SHADOW_MASK_DUMP=folder: a synchronous readback of both targets (diagnostic only; stalls the device).</summary>
    private void DumpIfAsked()
    {
        string folder = System.Environment.GetEnvironmentVariable("GUO_SHADOW_MASK_DUMP");
        if (string.IsNullOrEmpty(folder) || (_frame > 40 && _frame % 120 != 0)) return;
        System.IO.Directory.CreateDirectory(folder);
        foreach (var (rid, name) in new[] { (_shadow, "shadow"), (_occlusion, "occlusion") })
        {
            byte[] data = _rd.TextureGetData(rid, 0);
            using var image = Image.CreateFromData(_width, _height, false, Image.Format.R8, data);
            image.SavePng(System.IO.Path.Combine(folder, $"mask-{_frame:000}-{name}.png"));
        }
    }

    private void Fail(Exception e)
    {
        Supported = false; Failed = true;
        GD.PrintErr("[projected-shadows] RD mask failed, sun mask disabled: " + e.Message);
        Dispose();
    }

    private void EnsureDevice(int width, int height)
    {
        _rd ??= RenderingServer.GetRenderingDevice() ?? throw new NotSupportedException("No main RenderingDevice.");
        if (!_shader.IsValid)
        {
            using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl, SourceVertex = VertexGlsl, SourceFragment = FragmentGlsl };
            using RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, true);
            if (!string.IsNullOrEmpty(spirv.CompileErrorVertex) || !string.IsNullOrEmpty(spirv.CompileErrorFragment))
                throw new InvalidOperationException(spirv.CompileErrorVertex + "\n" + spirv.CompileErrorFragment);
            _shader = _rd.ShaderCreateFromSpirV(spirv, "guo_projected_shadow_mask");
            Require(_shader, "shader");
            using var sampler = new RDSamplerState
            {
                MagFilter = RD.SamplerFilter.Nearest, MinFilter = RD.SamplerFilter.Nearest, MipFilter = RD.SamplerFilter.Nearest,
                RepeatU = RD.SamplerRepeatMode.ClampToEdge, RepeatV = RD.SamplerRepeatMode.ClampToEdge, RepeatW = RD.SamplerRepeatMode.ClampToEdge,
                MinLod = 0, MaxLod = 0, UseAnisotropy = false, EnableCompare = false,
            };
            _sampler = _rd.SamplerCreate(sampler);
            Require(_sampler, "sampler");
            using var whiteFormat = new RDTextureFormat
            {
                Format = RD.DataFormat.R8G8B8A8Unorm, Width = 1, Height = 1, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
                TextureType = RD.TextureType.Type2D, Samples = RD.TextureSamples.Samples1,
                UsageBits = RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanUpdateBit,
            };
            using var whiteView = new RDTextureView();
            _white = _rd.TextureCreate(whiteFormat, whiteView, new Godot.Collections.Array<byte[]> { new byte[] { 255, 255, 255, 255 } });
            Require(_white, "white texture");
        }
        if (_width != width || _height != height || !_shadow.IsValid)
        {
            FreeTargets();
            var usage = RD.TextureUsageBits.ColorAttachmentBit | RD.TextureUsageBits.SamplingBit
                | RD.TextureUsageBits.CanCopyFromBit;
            if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.R8Unorm, usage))
                throw new NotSupportedException("R8 UNORM attachment unsupported.");
            _shadow = CreateTarget(width, height, usage);
            _occlusion = CreateTarget(width, height, usage);
            _shadowFramebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _shadow }, RD.InvalidId, 1);
            Require(_shadowFramebuffer, "shadow framebuffer");
            _occlusionFramebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _occlusion }, RD.InvalidId, 1);
            Require(_occlusionFramebuffer, "occlusion framebuffer");
            if (!_pipeline.IsValid)
            {
                using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
                using var samples = new RDPipelineMultisampleState { SampleCount = RD.TextureSamples.Samples1 };
                using var depth = new RDPipelineDepthStencilState { EnableDepthTest = false, EnableDepthWrite = false, EnableStencil = false };
                using var attachment = new RDPipelineColorBlendStateAttachment
                {
                    EnableBlend = true,
                    SrcColorBlendFactor = RD.BlendFactor.One, DstColorBlendFactor = RD.BlendFactor.One, ColorBlendOp = RD.BlendOperation.Maximum,
                    SrcAlphaBlendFactor = RD.BlendFactor.One, DstAlphaBlendFactor = RD.BlendFactor.One, AlphaBlendOp = RD.BlendOperation.Maximum,
                    WriteR = true, WriteG = true, WriteB = true, WriteA = true,
                };
                using var blend = new RDPipelineColorBlendState { EnableLogicOp = false, Attachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
                _pipeline = _rd.RenderPipelineCreate(_shader, _rd.FramebufferGetFormat(_shadowFramebuffer), RD.InvalidId,
                    RD.RenderPrimitive.Triangles, raster, samples, depth, blend, (RD.PipelineDynamicStateFlags)0, 0,
                    new Godot.Collections.Array<RDPipelineSpecializationConstant>());
                Require(_pipeline, "pipeline");
                if (!_rd.RenderPipelineIsValid(_pipeline)) throw new InvalidOperationException("Invalid mask pipeline.");
            }
            _width = width; _height = height;
            if (_publish)
            {
                _shadowTexture.TextureRdRid = _shadow;
                _occlusionTexture.TextureRdRid = _occlusion;
            }
        }
    }

    private Rid CreateTarget(int width, int height, RD.TextureUsageBits usage)
    {
        using var format = new RDTextureFormat
        {
            Format = RD.DataFormat.R8Unorm, Width = (uint)width, Height = (uint)height, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            TextureType = RD.TextureType.Type2D, Samples = RD.TextureSamples.Samples1, UsageBits = usage,
        };
        using var view = new RDTextureView();
        Rid texture = _rd.TextureCreate(format, view, new Godot.Collections.Array<byte[]>());
        Require(texture, "mask target");
        return texture;
    }

    private static void Require(Rid rid, string name)
    {
        if (!rid.IsValid) throw new InvalidOperationException("Could not create " + name);
    }

    private void Draw()
    {
        int total = _stamps.Count + _occluders.Count;
        uint bytes = checked((uint)total * QuadBytes);
        if (bytes > _storageCapacity)
        {
            if (_storageSet.IsValid) _rd.FreeRid(_storageSet);
            _storageSet = default;
            if (_storage.IsValid) _rd.FreeRid(_storage);
            uint next = Math.Max(65536u, _storageCapacity);
            while (next < bytes) next = checked(next * 2);
            _storage = _rd.StorageBufferCreate(next, ReadOnlySpan<byte>.Empty, (RD.StorageBufferUsage)0, (RD.BufferCreationBits)0);
            Require(_storage, "quad buffer");
            _storageCapacity = next;
        }
        if (!_storageSet.IsValid)
        {
            using var u = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
            u.AddId(_storage);
            _storageSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _shader, 0);
            Require(_storageSet, "quad set");
        }
        if (_upload.Length < bytes) _upload = new byte[Math.Max(bytes, (uint)_upload.Length * 2)];
        if (_order.Length < total) { _order = new int[total]; _orderKeys = new ulong[total]; }

        // Grouped by texture: MAX blending makes the submission order irrelevant.
        int stampRuns = Pack(_stamps, _stampTextures, 0);
        int occluderRuns = Pack(_occluders, _occluderTextures, _stamps.Count);
        Error error = _rd.BufferUpdate(_storage, 0, bytes, _upload.AsSpan(0, (int)bytes));
        if (error != Error.Ok) throw new InvalidOperationException("Quad BufferUpdate: " + error);

        // Uniform sets for every texture this frame, outside the open draw lists.
        for (int i = 0; i < total; i++)
            if (i == 0 || i == _stamps.Count || _orderKeys[i] != _orderKeys[i - 1]) TextureSet(QuadTexture(i));

        DrawRuns(_shadowFramebuffer, 0, 0u, _stamps.Count);
        DrawRuns(_occlusionFramebuffer, _stamps.Count, 1u, _occluders.Count);

        _expired.Clear();
        foreach (var entry in _textureSets)
            if (entry.Value.frame < _frame - 120 || !_rd.UniformSetIsValid(entry.Value.set)) _expired.Add(entry.Key);
        foreach (Rid key in _expired)
        {
            if (_rd.UniformSetIsValid(_textureSets[key].set)) _rd.FreeRid(_textureSets[key].set);
            _textureSets.Remove(key);
        }
    }

    private Rid QuadTexture(int packed) => packed < _stamps.Count
        ? _stampTextures[_order[packed]] : _occluderTextures[_order[packed]];

    /// <summary>Sort one list by texture and write its quads at <paramref name="offset"/> records into the upload.</summary>
    private int Pack(List<Quad> quads, List<Rid> textures, int offset)
    {
        int count = quads.Count;
        if (count == 0) return 0;
        var order = _order.AsSpan(offset, count);
        var keys = _orderKeys.AsSpan(offset, count);
        for (int i = 0; i < count; i++) { order[i] = i; keys[i] = textures[i].Id; }
        keys.Sort(order);
        var records = MemoryMarshal.Cast<byte, Quad>(_upload.AsSpan(offset * QuadBytes, count * QuadBytes));
        int runs = 1;
        for (int i = 0; i < count; i++)
        {
            records[i] = quads[order[i]];
            if (i > 0 && keys[i] != keys[i - 1]) runs++;
        }
        return runs;
    }

    private void DrawRuns(Rid framebuffer, int first, uint mode, int count)
    {
        long list = _rd.DrawListBegin(framebuffer, RD.DrawFlags.ClearColor0, _clear, 1.0f, 0, null, 0);
        if (list == RD.InvalidId) throw new InvalidOperationException("DrawListBegin failed.");
        try
        {
            if (count == 0) return;
            _rd.DrawListBindRenderPipeline(list, _pipeline);
            _rd.DrawListBindUniformSet(list, _storageSet, 0);
            Span<Push> push = stackalloc Push[1];
            push[0].ScreenX = _width; push[0].ScreenY = _height; push[0].Mode = mode;
            int runStart = 0;
            ulong runKey = _orderKeys[first];
            for (int i = 1; i <= count; i++)
            {
                if (i < count && _orderKeys[first + i] == runKey) continue;
                push[0].First = (uint)((first + runStart) * 6);
                _rd.DrawListBindUniformSet(list, TextureSet(QuadTexture(first + runStart)), 1);
                _rd.DrawListSetPushConstant(list, MemoryMarshal.AsBytes(push), 32);
                _rd.DrawListDraw(list, false, 1, (uint)((i - runStart) * 6));
                if (i < count) { runStart = i; runKey = _orderKeys[first + i]; }
            }
        }
        finally { _rd.DrawListEnd(); }
    }

    private Rid TextureSet(Rid texture)
    {
        if (_textureSets.TryGetValue(texture, out var cached) && _rd.UniformSetIsValid(cached.set))
        {
            _textureSets[texture] = (cached.set, _frame);
            return cached.set;
        }
        Rid raw = texture.IsValid ? RenderingServer.TextureGetRdTexture(texture, false) : _white;
        if (!_rd.TextureIsValid(raw)) throw new InvalidOperationException("Borrowed art texture unavailable.");
        using var u = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 };
        u.AddId(_sampler); u.AddId(raw);
        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _shader, 1);
        Require(set, "art set");
        _textureSets[texture] = (set, _frame);
        return set;
    }

    private void FreeTargets()
    {
        _shadowTexture.TextureRdRid = default;
        _occlusionTexture.TextureRdRid = default;
        Free(ref _shadowFramebuffer); Free(ref _occlusionFramebuffer);
        Free(ref _shadow); Free(ref _occlusion);
        _width = _height = 0;
    }

    private void Free(ref Rid rid)
    {
        if (rid.IsValid && _rd != null) _rd.FreeRid(rid);
        rid = default;
    }

    internal void DisposeAll() { Dispose(); _shadowTexture.Dispose(); _occlusionTexture.Dispose(); }

    public void Dispose()
    {
        if (_rd == null) return;
        foreach (var entry in _textureSets) if (_rd.UniformSetIsValid(entry.Value.set)) _rd.FreeRid(entry.Value.set);
        _textureSets.Clear();
        FreeTargets();
        Free(ref _storageSet); Free(ref _storage); _storageCapacity = 0;
        Free(ref _pipeline); Free(ref _white); Free(ref _sampler); Free(ref _shader);
        _stamps.Clear(); _occluders.Clear(); _stampTextures.Clear(); _occluderTextures.Clear();
    }
}
