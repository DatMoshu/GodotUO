// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;

namespace GUO.Labs.GpuRenderer;

internal sealed class OrderedWorldDevice
{
    // Four 48-byte corners per quad. The vertex shader expands the selected diagonal.
    // Position is batcher-local AFTER its applicable item/world offset.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vertex
    {
        public Vector4 PositionUv; // xy position, zw normalized UV
        public Vector4 PackedHue;  // existing encoded modulate RGBA, transport precision matched
        public Vector4 Normal;     // xyz interpolated land normal; w selects the canvas-rect diagonal
    }

    // Four 16-byte blocks, 64 bytes total; exactly matches the shader push block.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Push
    {
        public Vector4 Row0;       // matrix xx, yx, origin.x, brightlight
        public Vector4 Row1;       // matrix xy, yy, origin.y, circle radius in target pixels
        public Vector4 Screen;     // width, height, circle center x, circle center y
        public uint FirstVertex;
        public uint Reserved0, Reserved1, Reserved2;
    }

    // These are RenderingServer texture RIDs, not RD RIDs. Hold their Texture2D
    // owners alive in the immutable frame ticket. All three textures must exist.
    public readonly record struct Textures(Rid ArtRs, Rid HueRs, Rid LightRs);
    public readonly record struct Segment(uint FirstVertex, uint VertexCount, Textures Textures, Transform2D Transform);

    // Create the wrapper on the main thread. The API's C# spelling is Texture2Drd.
    public Texture2Drd Presenter { get; } = new();
    public Vector2I Size { get; private set; }
    private RD _rd;
    private Rid _shader, _pipeline, _color, _framebuffer, _sampler, _storage, _storageSet;
    private Rid _retiredColor;
    private uint _capacityBytes;
    private readonly Dictionary<(Rid, Rid, Rid), Rid> _textureSets = new();
    private readonly Dictionary<Textures, (Rid, Rid, Rid)> _resolvedTextures = new();
    private readonly HashSet<(Rid, Rid, Rid)> _usedTextureSets = new();
    private readonly List<(Rid, Rid, Rid)> _expiredTextureSets = new();
    private readonly List<Rid> _frameTextureSets = new();
    private readonly Color[] _clear = new Color[1];

    // ALL methods with RT suffix run through RenderingServer.CallOnRenderThread.
    // Initialization/resize is a separate transaction; use canvas until published ready.
    public void InitializeRT(Vector2I size, string vertexGlsl, string fragmentGlsl)
    {
        if (_color.IsValid || _shader.IsValid)
            throw new InvalidOperationException("Retire the previous target before initialization.");
        if (size.X <= 0 || size.Y <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        _rd = RenderingServer.GetRenderingDevice()
            ?? throw new NotSupportedException("No main RenderingDevice; keep canvas fallback.");
        if (Marshal.SizeOf<Vertex>() != 48 || Marshal.SizeOf<Push>() != 64)
            throw new InvalidOperationException("CPU/shader layout mismatch.");
        Size = size;
        try
        {
            var usage = RD.TextureUsageBits.ColorAttachmentBit | RD.TextureUsageBits.SamplingBit
                | RD.TextureUsageBits.CanCopyFromBit;
            if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.R8G8B8A8Unorm, usage))
                throw new NotSupportedException("RGBA8 UNORM attachment/sampling unsupported.");
            using var format = new RDTextureFormat
            {
                Format = RD.DataFormat.R8G8B8A8Unorm,
                Width = (uint)size.X, Height = (uint)size.Y, Depth = 1,
                ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
                Samples = RD.TextureSamples.Samples1, UsageBits = usage,
                IsDiscardable = false,
            };
            using var view = new RDTextureView();
            var initialTextureData = new Godot.Collections.Array<byte[]>();
            _color = _rd.TextureCreate(format, view, initialTextureData);
            Require(_color, "color texture");
            var attachments = new Godot.Collections.Array<Rid> { _color };
            _framebuffer = _rd.FramebufferCreate(attachments, RD.InvalidId, 1);
            Require(_framebuffer, "framebuffer");

            using var sampler = new RDSamplerState
            {
                MagFilter = RD.SamplerFilter.Nearest, MinFilter = RD.SamplerFilter.Nearest,
                MipFilter = RD.SamplerFilter.Nearest,
                RepeatU = RD.SamplerRepeatMode.ClampToEdge,
                RepeatV = RD.SamplerRepeatMode.ClampToEdge,
                RepeatW = RD.SamplerRepeatMode.ClampToEdge,
                MinLod = 0, MaxLod = 0, UseAnisotropy = false, EnableCompare = false,
            };
            _sampler = _rd.SamplerCreate(sampler);
            Require(_sampler, "nearest sampler");

            using var source = new RDShaderSource
            {
                Language = RD.ShaderLanguage.Glsl,
                SourceVertex = vertexGlsl, SourceFragment = fragmentGlsl,
            };
            using RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, true);
            if (!string.IsNullOrEmpty(spirv.CompileErrorVertex)
                || !string.IsNullOrEmpty(spirv.CompileErrorFragment))
                throw new InvalidOperationException(spirv.CompileErrorVertex + "\n" + spirv.CompileErrorFragment);
            _shader = _rd.ShaderCreateFromSpirV(spirv, "guo_ordered_world_lab");
            Require(_shader, "shader");

            using var raster = new RDPipelineRasterizationState
            {
                CullMode = RD.PolygonCullMode.Disabled, Wireframe = false,
                DiscardPrimitives = false, EnableDepthClamp = false,
            };
            using var samples = new RDPipelineMultisampleState
            {
                SampleCount = RD.TextureSamples.Samples1, EnableSampleShading = false,
                EnableAlphaToCoverage = false, EnableAlphaToOne = false,
            };
            using var depth = new RDPipelineDepthStencilState
            {
                EnableDepthTest = false, EnableDepthWrite = false, EnableStencil = false,
            };
            using var blendAttachment = new RDPipelineColorBlendStateAttachment
            {
                EnableBlend = true,
                SrcColorBlendFactor = RD.BlendFactor.One,
                DstColorBlendFactor = RD.BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = RD.BlendOperation.Add,
                SrcAlphaBlendFactor = RD.BlendFactor.One,
                DstAlphaBlendFactor = RD.BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = RD.BlendOperation.Add,
                WriteR = true, WriteG = true, WriteB = true, WriteA = true,
            };
            var blendAttachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { blendAttachment };
            using var blend = new RDPipelineColorBlendState { EnableLogicOp = false, Attachments = blendAttachments };
            var specializations = new Godot.Collections.Array<RDPipelineSpecializationConstant>();
            _pipeline = _rd.RenderPipelineCreate(_shader, _rd.FramebufferGetFormat(_framebuffer),
                RD.InvalidId, RD.RenderPrimitive.Triangles, raster, samples, depth, blend,
                (RD.PipelineDynamicStateFlags)0, 0, specializations);
            Require(_pipeline, "pipeline");
            if (!_rd.RenderPipelineIsValid(_pipeline)) throw new InvalidOperationException("Invalid pipeline.");
            Presenter.TextureRdRid = _color; // RS wrapper of our owned main-device texture.
            Free(ref _retiredColor); // Replace the wrapper's dependent view before retiring its old source.
        }
        catch { DisposeRT(); throw; }
    }

    private static void Require(Rid rid, string name)
    {
        if (!rid.IsValid) throw new InvalidOperationException("Could not create/resolve " + name);
    }

    private void EnsureStorageRT(uint neededBytes)
    {
        if (neededBytes <= _capacityBytes) return;
        uint capacity = Math.Max(4096u, _capacityBytes);
        while (capacity < neededBytes) capacity = checked(capacity * 2);
        Free(ref _storageSet);
        Free(ref _storage);
        _capacityBytes = 0;
        _storage = _rd.StorageBufferCreate(capacity, ReadOnlySpan<byte>.Empty,
            (RD.StorageBufferUsage)0, (RD.BufferCreationBits)0);
        Require(_storage, "storage buffer");
        using var u = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
        u.AddId(_storage);
        var uniforms = new Godot.Collections.Array<RDUniform> { u };
        _storageSet = _rd.UniformSetCreate(uniforms, _shader, 0);
        Require(_storageSet, "storage set");
        _capacityBytes = capacity;
    }

    private Rid TextureSetRT(Textures textures)
    {
        // false selects the raw view: palette/art bytes must not be sRGB-decoded.
        if (!_resolvedTextures.TryGetValue(textures, out var key))
        {
            Rid art = RenderingServer.TextureGetRdTexture(textures.ArtRs, false);
            Rid hue = RenderingServer.TextureGetRdTexture(textures.HueRs, false);
            Rid light = RenderingServer.TextureGetRdTexture(textures.LightRs, false);
            if (!_rd.TextureIsValid(art) || !_rd.TextureIsValid(hue) || !_rd.TextureIsValid(light))
                throw new InvalidOperationException("Borrowed texture unavailable; use canvas for the entire frame.");
            key = (art, hue, light);
            _resolvedTextures.Add(textures, key);
        }
        _usedTextureSets.Add(key);
        if (_textureSets.TryGetValue(key, out Rid existing) && _rd.UniformSetIsValid(existing))
            return existing;
        // An invalid existing set may already have been destroyed with its dependency.
        using var a = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 }, key.Item1);
        using var h = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 1 }, key.Item2);
        using var l = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 2 }, key.Item3);
        var uniforms = new Godot.Collections.Array<RDUniform> { a, h, l };
        Rid set = _rd.UniformSetCreate(uniforms, _shader, 1);
        Require(set, "texture set");
        _textureSets[key] = set;
        return set;
    }

    private RDUniform SamplerUniform(RDUniform u, Rid texture)
    {
        u.AddId(_sampler); // Combined sampler binding order: sampler, then texture.
        u.AddId(texture);
        return u;
    }

    // Each call records exactly one target image for one immutable frame ticket.
    // opaqueBackdrop=true for ALL world content; false is diagnostic sorted-only
    // compositing and is not generally byte-exact for translucent pixels.
    public void RenderRT(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<Segment> segments,
        in Push frame, bool opaqueBackdrop)
    {
        if (frame.Screen.X != Size.X || frame.Screen.Y != Size.Y)
            throw new InvalidOperationException("Stale target dimensions.");
        _frameTextureSets.Clear();
        _usedTextureSets.Clear();
        _resolvedTextures.Clear();
        uint next = 0;
        foreach (Segment segment in segments)
        {
            if (segment.FirstVertex != next || segment.VertexCount == 0 || segment.VertexCount % 6 != 0)
                throw new InvalidOperationException("Segments must partition the original ordered triangle stream.");
            next = checked(next + segment.VertexCount);
            if (next / 6 * 4 > (uint)vertices.Length) throw new InvalidOperationException("Out-of-range draw.");
            _frameTextureSets.Add(TextureSetRT(segment.Textures));
        }
        if (vertices.Length % 4 != 0 || next / 6 * 4 != (uint)vertices.Length) throw new InvalidOperationException("Incomplete command stream.");
        if (!vertices.IsEmpty)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(vertices);
            EnsureStorageRT((uint)bytes.Length);
            Error error = _rd.BufferUpdate(_storage, 0, (uint)bytes.Length, bytes);
            if (error != Error.Ok) throw new InvalidOperationException("BufferUpdate: " + error);
        }

        // No uploads, allocations or texture discovery inside the open draw list.
        _clear[0] = opaqueBackdrop ? Colors.Black : Colors.Transparent;
        long list = _rd.DrawListBegin(_framebuffer, RD.DrawFlags.ClearColor0, _clear, 1.0f, 0, null, 0);
        if (list == RD.InvalidId) throw new InvalidOperationException("DrawListBegin failed.");
        try
        {
            if (!vertices.IsEmpty)
            {
                _rd.DrawListBindRenderPipeline(list, _pipeline);
                _rd.DrawListBindUniformSet(list, _storageSet, 0);
                Span<Push> push = stackalloc Push[1];
                for (int i = 0; i < segments.Length; i++)
                {
                    push[0] = frame;
                    Transform2D transform = segments[i].Transform;
                    push[0].Row0 = new Vector4(transform.X.X, transform.Y.X, transform.Origin.X, frame.Row0.W);
                    push[0].Row1 = new Vector4(transform.X.Y, transform.Y.Y, transform.Origin.Y, frame.Row1.W);
                    push[0].FirstVertex = segments[i].FirstVertex;
                    _rd.DrawListBindUniformSet(list, _frameTextureSets[i], 1);
                    _rd.DrawListSetPushConstant(list, MemoryMarshal.AsBytes(push), 64);
                    // Fourth argument is a procedural VERTEX COUNT, not an offset.
                    // Base vertex is our push constant. No index/vertex array is bound.
                    _rd.DrawListDraw(list, false, 1, segments[i].VertexCount);
                }
            }
        }
        finally { _rd.DrawListEnd(); }
        // Uniform cache bounded to this frame's borrowed textures.
        _expiredTextureSets.Clear();
        foreach (var entry in _textureSets)
            if (!_usedTextureSets.Contains(entry.Key)) _expiredTextureSets.Add(entry.Key);
        foreach (var key in _expiredTextureSets)
        {
            Rid set = _textureSets[key];
            if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
            _textureSets.Remove(key);
        }
        // Main RD submission is owned by Godot: do not call Submit(), Sync() or GetData().
    }

    private void Free(ref Rid owned)
    {
        if (owned.IsValid) _rd.FreeRid(owned);
        owned = default;
    }

    // Caller first removes this presenter's canvas commands and invalidates tickets.
    // Do not call DisposeRT twice while a frame callback still references this object.
    public void DisposeRT(bool detachPresenter = true)
    {
        if (_rd == null) return;
        // Replacement must retain the RS wrapper RID already queued by this frame.
        if (detachPresenter) Presenter.TextureRdRid = default;
        foreach (Rid set in _textureSets.Values)
            if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
        _textureSets.Clear();
        _resolvedTextures.Clear();
        Free(ref _storageSet);
        Free(ref _storage);
        Free(ref _pipeline);
        Free(ref _shader);
        Free(ref _framebuffer);
        if (detachPresenter)
        {
            Free(ref _color);
            Free(ref _retiredColor);
        }
        else
        {
            _retiredColor = _color;
            _color = default;
        }
        Free(ref _sampler);
        _capacityBytes = 0;
        Size = default;
        // Dispose Presenter on the owning thread after this transaction completes.
        // Never free atlas/hue/light RIDs borrowed through TextureGetRdTexture.
    }
}

#endif
