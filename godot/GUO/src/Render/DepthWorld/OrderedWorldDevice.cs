// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.DepthWorld;

internal sealed class OrderedWorldDevice
{
    // Transport-only values live in the optimized GUO assembly. GodotSharp's
    // editor Debug value constructors need not run for every captured corner.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Float4 : IEquatable<Float4>
    {
        public float X, Y, Z, W;
        public Float4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public readonly bool Equals(Float4 other) => X.Equals(other.X) && Y.Equals(other.Y)
            && Z.Equals(other.Z) && W.Equals(other.W);
        public override readonly bool Equals(object obj) => obj is Float4 other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, W);
    }

    // Four 48-byte corners per quad. The vertex shader expands the selected diagonal.
    // Position is batcher-local; the occurrence state supplies its exact transform.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vertex
    {
        public Float4 PositionUv; // xy position, zw normalized UV
        public Float4 PackedHue;  // existing encoded modulate RGBA, transport precision matched
        public Float4 Normal;     // xyz interpolated land normal; w selects the canvas-rect diagonal
    }

    // One exact composed transform per ordered occurrence, never per resident
    // geometry slot. Plain floats avoid per-quad GodotSharp value constructors.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct State
    {
        public float Xx, Yx, OriginX;
        public uint ArtSlot;
        public float Xy, Yy, OriginY, Pad1;
    }

    // The geometry cache has a separate unique-quad bound. Repeated geometry
    // may appear more often, but the occurrence buffers must remain bounded too.
    internal const int MaxOccurrences = 262144;

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
    internal const int ArtSlots = 4;

    // Original views only. Unused slots repeat Art0, so every descriptor is valid.
    // The same shape keys both captured RS bindings and their validated raw RD views.
    public readonly record struct TextureSet(Rid Art0, Rid Art1, Rid Art2, Rid Art3, Rid Hue, Rid Light)
    {
        public TextureSet(Textures textures) : this(textures.ArtRs, textures.ArtRs, textures.ArtRs,
            textures.ArtRs, textures.HueRs, textures.LightRs) { }

        public int FindArt(Rid art, int count)
        {
            if (Art0 == art) return 0;
            if (count > 1 && Art1 == art) return 1;
            if (count > 2 && Art2 == art) return 2;
            if (count > 3 && Art3 == art) return 3;
            return -1;
        }

        public TextureSet WithArt(int slot, Rid art) => slot switch
        {
            1 => this with { Art1 = art },
            2 => this with { Art2 = art },
            3 => this with { Art3 = art },
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
    }

    public struct Segment
    {
        public uint FirstVertex, VertexCount;
        public TextureSet Textures;
        public Segment(uint firstVertex, uint vertexCount, TextureSet textures)
        { FirstVertex = firstVertex; VertexCount = vertexCount; Textures = textures; }
    }

    // Create the wrapper on the main thread. The API's C# spelling is Texture2Drd.
    public Texture2Drd Presenter { get; } = new();
    public Vector2I Size { get; private set; }
    private RD _rd;
    private Rid _shader, _pipeline, _color, _framebuffer, _sampler, _storage, _orderStorage, _stateStorage, _storageSet;
    private Rid _retiredColor;
    // ADR-0044 Depth World: a depth attachment, and chunk land/statics drawn
    // from resident buffers by a second pipeline before the ordered stream.
    private bool _depthMode;
    private Rid _depth, _residentShader, _residentPipeline, _residentQuads, _residentVisible, _residentSet;
    private uint _residentQuadCapacity, _residentVisibleCapacity;
    internal static long ResidentBytes, ResidentDrawCalls;
    private uint _capacityBytes, _orderCapacityBytes, _stateCapacityBytes;
    private byte[] _appliedStates = Array.Empty<byte>();
    private int _appliedStateBytes;
    private bool _statesValid;
    private readonly OrderedWorldResidency _resident = new();
    internal static long GeometryBytes, OrderBytes, UploadCalls, DirtyQuads, BufferGrowths, CacheEvictions;
    internal static long StateBytes, StateUploadCalls, ChangedStateStreams, StateBufferGrowths, TextureRuns, TextureBatches, Occurrences;
    // Latest transaction for this diagnostic device, not a sum over instances.
    internal static uint ActiveStateBytes, StateCapacityBytes;
    internal static int CachedQuads, PeakCachedQuads;
    private readonly Dictionary<TextureSet, Rid> _textureSets = new();
    private readonly Dictionary<TextureSet, TextureSet> _resolvedTextures = new();
    private readonly HashSet<TextureSet> _usedTextureSets = new();
    private readonly List<TextureSet> _expiredTextureSets = new();
    private readonly List<Rid> _frameTextureSets = new();
    private readonly Color[] _clear = new Color[1];

    // ALL methods with RT suffix run through RenderingServer.CallOnRenderThread.
    // Initialization/resize is a separate transaction; use canvas until published ready.
    public void InitializeRT(Vector2I size, string vertexGlsl, string fragmentGlsl, string residentVertexGlsl = null)
    {
        _depthMode = residentVertexGlsl != null;
        if (_color.IsValid || _shader.IsValid)
            throw new InvalidOperationException("Retire the previous target before initialization.");
        if (size.X <= 0 || size.Y <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        _rd = RenderingServer.GetRenderingDevice()
            ?? throw new NotSupportedException("No main RenderingDevice; keep canvas fallback.");
        if (Marshal.SizeOf<Vertex>() != 48 || Marshal.SizeOf<Float4>() != 16
            || Marshal.OffsetOf<Vertex>(nameof(Vertex.PositionUv)).ToInt32() != 0
            || Marshal.OffsetOf<Vertex>(nameof(Vertex.PackedHue)).ToInt32() != 16
            || Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal)).ToInt32() != 32
            || Marshal.SizeOf<Push>() != 64
            || Marshal.SizeOf<State>() != 32 || Marshal.OffsetOf<State>(nameof(State.Xy)).ToInt32() != 16
            || Marshal.OffsetOf<State>(nameof(State.ArtSlot)).ToInt32() != 12
            || Marshal.OffsetOf<State>(nameof(State.Xx)).ToInt32() != 0
            || Marshal.OffsetOf<State>(nameof(State.Yx)).ToInt32() != 4
            || Marshal.OffsetOf<State>(nameof(State.OriginX)).ToInt32() != 8
            || Marshal.OffsetOf<State>(nameof(State.Yy)).ToInt32() != 20
            || Marshal.OffsetOf<State>(nameof(State.OriginY)).ToInt32() != 24)
            throw new InvalidOperationException("CPU/shader layout mismatch.");
        // Four literal art bindings plus hue/light. No descriptor-indexing extension.
        if (_rd.LimitGet(RD.Limit.MaxTexturesPerUniformSet) < ArtSlots + 2
            || _rd.LimitGet(RD.Limit.MaxSamplersPerUniformSet) < ArtSlots + 2
            || _rd.LimitGet(RD.Limit.MaxTexturesPerShaderStage) < ArtSlots + 2
            || _rd.LimitGet(RD.Limit.MaxSamplersPerShaderStage) < ArtSlots + 2)
            throw new NotSupportedException("Six texture/sampler descriptors are required; use canvas.");
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
            if (_depthMode)
            {
                var depthUsage = RD.TextureUsageBits.DepthStencilAttachmentBit;
                if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.D32Sfloat, depthUsage))
                    throw new NotSupportedException("D32 depth attachment unsupported.");
                using var depthFormat = new RDTextureFormat
                {
                    Format = RD.DataFormat.D32Sfloat,
                    Width = (uint)size.X, Height = (uint)size.Y, Depth = 1,
                    ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
                    Samples = RD.TextureSamples.Samples1, UsageBits = depthUsage,
                };
                using var depthView = new RDTextureView();
                _depth = _rd.TextureCreate(depthFormat, depthView, new Godot.Collections.Array<byte[]>());
                Require(_depth, "depth texture");
                attachments.Add(_depth);
            }
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
            OrderedWorldShaderContract.Validate(spirv.BytecodeVertex, spirv.BytecodeFragment);
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
            // ADR-0044 1: LessEqual, depth write on for every category.
            using var depth = new RDPipelineDepthStencilState
            {
                EnableDepthTest = _depthMode, EnableDepthWrite = _depthMode, EnableStencil = false,
                DepthCompareOperator = RD.CompareOperator.LessOrEqual,
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
            if (_depthMode)
            {
                using var residentSource = new RDShaderSource
                {
                    Language = RD.ShaderLanguage.Glsl,
                    SourceVertex = residentVertexGlsl, SourceFragment = fragmentGlsl,
                };
                using RDShaderSpirV residentSpirv = _rd.ShaderCompileSpirVFromSource(residentSource, true);
                if (!string.IsNullOrEmpty(residentSpirv.CompileErrorVertex)
                    || !string.IsNullOrEmpty(residentSpirv.CompileErrorFragment))
                    throw new InvalidOperationException(residentSpirv.CompileErrorVertex + "\n" + residentSpirv.CompileErrorFragment);
                _residentShader = _rd.ShaderCreateFromSpirV(residentSpirv, "guo_depth_world_resident");
                Require(_residentShader, "resident shader");
                _residentPipeline = _rd.RenderPipelineCreate(_residentShader, _rd.FramebufferGetFormat(_framebuffer),
                    RD.InvalidId, RD.RenderPrimitive.Triangles, raster, samples, depth, blend,
                    (RD.PipelineDynamicStateFlags)0, 0, specializations);
                Require(_residentPipeline, "resident pipeline");
            }
            Presenter.TextureRdRid = _color; // RS wrapper of our owned main-device texture.
            Free(ref _retiredColor); // Replace the wrapper's dependent view before retiring its old source.
        }
        catch { DisposeRT(); throw; }
    }

    private static void Require(Rid rid, string name)
    {
        if (!rid.IsValid) throw new InvalidOperationException("Could not create/resolve " + name);
    }

    private void EnsureStorageRT(uint neededBytes, uint neededOrderBytes, uint neededStateBytes)
    {
        if (neededBytes > _capacityBytes || neededOrderBytes > _orderCapacityBytes || neededStateBytes > _stateCapacityBytes)
            Free(ref _storageSet);
        if (neededBytes > _capacityBytes)
        {
            _resident.InvalidateGeometry(); // A new allocation has no applied slots.
            GrowBuffer(ref _storage, ref _capacityBytes, neededBytes);
        }
        if (neededOrderBytes > _orderCapacityBytes)
        {
            _resident.InvalidateOrder(); // Identical IDs still need upload into a new buffer.
            GrowBuffer(ref _orderStorage, ref _orderCapacityBytes, neededOrderBytes);
        }
        if (neededStateBytes > _stateCapacityBytes)
        {
            _statesValid = false;
            GrowBuffer(ref _stateStorage, ref _stateCapacityBytes, neededStateBytes);
            StateBufferGrowths++;
        }
        StateCapacityBytes = _stateCapacityBytes;
        if (_storageSet.IsValid) return;
        using var u = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
        u.AddId(_storage);
        using var order = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 1 };
        order.AddId(_orderStorage);
        using var state = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 2 };
        state.AddId(_stateStorage);
        var uniforms = new Godot.Collections.Array<RDUniform> { u, order, state };
        _storageSet = _rd.UniformSetCreate(uniforms, _shader, 0);
        Require(_storageSet, "storage set");
    }

    private void GrowBuffer(ref Rid buffer, ref uint capacity, uint needed)
    {
        uint next = Math.Max(4096u, capacity);
        while (next < needed) next = checked(next * 2);
        Free(ref buffer);
        capacity = 0;
        buffer = _rd.StorageBufferCreate(next, ReadOnlySpan<byte>.Empty,
            (RD.StorageBufferUsage)0, (RD.BufferCreationBits)0);
        Require(buffer, "resident storage buffer");
        capacity = next;
        BufferGrowths++;
    }

    private void UploadResidentRT(ReadOnlySpan<OrderedWorldResidency.Occurrence> occurrences, ReadOnlySpan<OrderedWorldResidency.Quad> dynamic, ReadOnlySpan<State> states)
    {
        long evictions = _resident.Evictions;
        try
        {
            _resident.Prepare(occurrences, dynamic);
            CachedQuads = _resident.Count;
            PeakCachedQuads = Math.Max(PeakCachedQuads, CachedQuads);
            if (_resident.Order.Length != states.Length) throw new InvalidOperationException("State/order length mismatch.");
            EnsureStorageRT(checked((uint)_resident.Count * 192), checked((uint)_resident.Order.Length * 4), checked((uint)states.Length * 32));
            int cursor = 0;
            while (_resident.NextDirty(ref cursor, out int start, out int count))
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_resident.Slots.Slice(start, count));
                Error error = _rd.BufferUpdate(_storage, checked((uint)start * 192), (uint)bytes.Length, bytes);
                if (error != Error.Ok) throw new InvalidOperationException("Geometry BufferUpdate: " + error);
                GeometryBytes += bytes.Length; UploadCalls++; DirtyQuads += count;
                OrderedWorldCapture.UploadedBytes += bytes.Length;
                _resident.AppliedRange(start, count);
                if (OrderedWorldCapture.InjectFailure == 3) throw new InvalidOperationException("injected during resident upload");
            }
            if (_resident.OrderDirty)
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_resident.Order);
                Error error = _rd.BufferUpdate(_orderStorage, 0, (uint)bytes.Length, bytes);
                if (error != Error.Ok) throw new InvalidOperationException("Order BufferUpdate: " + error);
                OrderBytes += bytes.Length; UploadCalls++;
                OrderedWorldCapture.UploadedBytes += bytes.Length;
                if (OrderedWorldCapture.InjectFailure == 4) throw new InvalidOperationException("injected during order upload");
                _resident.AppliedOrder();
            }
            ReadOnlySpan<byte> stateBytes = MemoryMarshal.AsBytes(states);
            if (!_statesValid || stateBytes.Length != _appliedStateBytes
                || !stateBytes.SequenceEqual(_appliedStates.AsSpan(0, _appliedStateBytes)))
            {
                if (_appliedStates.Length < stateBytes.Length)
                    Array.Resize(ref _appliedStates, checked((int)_stateCapacityBytes));
                if (OrderedWorldCapture.InjectFailure == 5) throw new InvalidOperationException("injected before state upload");
                Error error = _rd.BufferUpdate(_stateStorage, 0, (uint)stateBytes.Length, stateBytes);
                if (error != Error.Ok) throw new InvalidOperationException("State BufferUpdate: " + error);
                StateBytes += stateBytes.Length; StateUploadCalls++; UploadCalls++; ChangedStateStreams++;
                OrderedWorldCapture.UploadedBytes += stateBytes.Length;
                if (OrderedWorldCapture.InjectFailure == 6) throw new InvalidOperationException("injected after state upload");
                stateBytes.CopyTo(_appliedStates);
                _appliedStateBytes = stateBytes.Length;
                _statesValid = true;
            }
        }
        catch
        {
            // A partial failure never advances a validity claim. Retry all data
            // next time, even if the next frame's ordered IDs compare equal.
            _resident.InvalidateGpu();
            _statesValid = false;
            throw;
        }
        finally { CacheEvictions += _resident.Evictions - evictions; }
    }

    private void UploadResidentChunksRT(DepthWorldResident resident)
    {
        uint quadBytes = checked((uint)Math.Max(resident.QuadCount, 1) * DepthWorldResident.QuadBytes);
        uint visibleBytes = checked((uint)Math.Max(resident.MergedCount, 1) * 4);
        if (quadBytes > _residentQuadCapacity || visibleBytes > _residentVisibleCapacity)
        {
            Free(ref _residentSet);
            if (quadBytes > _residentQuadCapacity) GrowBuffer(ref _residentQuads, ref _residentQuadCapacity, quadBytes);
            if (visibleBytes > _residentVisibleCapacity) GrowBuffer(ref _residentVisible, ref _residentVisibleCapacity, visibleBytes);
            resident.Invalidate();
        }
        if (!_residentSet.IsValid)
        {
            using var q = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
            q.AddId(_residentQuads);
            using var v = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 1 };
            v.AddId(_residentVisible);
            _residentSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { q, v }, _residentShader, 0);
            Require(_residentSet, "resident set");
        }
        try
        {
            if (resident.AllDirty)
            {
                Upload(_residentQuads, MemoryMarshal.AsBytes(resident.Quads.AsSpan(0, resident.QuadCount)), 0);
                Upload(_residentVisible, MemoryMarshal.AsBytes(resident.Merged.AsSpan(0, resident.MergedCount)), 0);
            }
            else
            {
                foreach (var (start, count) in resident.DirtyQuads)
                    Upload(_residentQuads, MemoryMarshal.AsBytes(resident.Quads.AsSpan(start, count)), (uint)start * DepthWorldResident.QuadBytes);
                // The merged list is small and rebuilt whole when visibility changes.
                if (resident.MergedDirty)
                    Upload(_residentVisible, MemoryMarshal.AsBytes(resident.Merged.AsSpan(0, resident.MergedCount)), 0);
            }
            resident.Uploaded();
        }
        catch { resident.Invalidate(); throw; }
    }

    private void Upload(Rid buffer, ReadOnlySpan<byte> bytes, uint offset)
    {
        if (bytes.IsEmpty) return;
        Error error = _rd.BufferUpdate(buffer, offset, (uint)bytes.Length, bytes);
        if (error != Error.Ok) throw new InvalidOperationException("Resident BufferUpdate: " + error);
        ResidentBytes += bytes.Length; UploadCalls++;
    }

    private Rid ResolveTextureRT(Rid texture)
    {
        Rid raw = RenderingServer.TextureGetRdTexture(texture, false);
        if (!_rd.TextureIsValid(raw))
            throw new InvalidOperationException("Borrowed texture unavailable; use canvas for the entire frame.");
        return raw;
    }

    private Rid TextureSetRT(TextureSet textures) => TextureSetRT(textures, _shader, _textureSets);

    private readonly Dictionary<TextureSet, Rid> _residentTextureSets = new();

    private Rid TextureSetRT(TextureSet textures, Rid shader, Dictionary<TextureSet, Rid> cache)
    {
        // false selects the raw view: palette/art bytes must not be sRGB-decoded.
        if (!_resolvedTextures.TryGetValue(textures, out var key))
        {
            key = new TextureSet(ResolveTextureRT(textures.Art0), ResolveTextureRT(textures.Art1),
                ResolveTextureRT(textures.Art2), ResolveTextureRT(textures.Art3),
                ResolveTextureRT(textures.Hue), ResolveTextureRT(textures.Light));
            _resolvedTextures.Add(textures, key);
        }
        _usedTextureSets.Add(key);
        if (cache.TryGetValue(key, out Rid existing) && _rd.UniformSetIsValid(existing))
            return existing;
        // An invalid existing set may already have been destroyed with its dependency.
        using var a0 = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 }, key.Art0);
        using var a1 = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 1 }, key.Art1);
        using var a2 = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 2 }, key.Art2);
        using var a3 = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 3 }, key.Art3);
        using var h = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 4 }, key.Hue);
        using var l = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 5 }, key.Light);
        var uniforms = new Godot.Collections.Array<RDUniform> { a0, a1, a2, a3, h, l };
        Rid set = _rd.UniformSetCreate(uniforms, shader, 1);
        Require(set, "texture set");
        cache[key] = set;
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
    public void RenderRT(ReadOnlySpan<OrderedWorldResidency.Occurrence> occurrences, ReadOnlySpan<OrderedWorldResidency.Quad> dynamic,
        ReadOnlySpan<State> states, ReadOnlySpan<Segment> segments, int textureRuns, in Push frame, bool opaqueBackdrop,
        DepthWorldResident resident = null, int cameraX = 0, int cameraY = 0, int maxGroundZ = 127, int landEnd = 0)
    {
        if (frame.Screen.X != Size.X || frame.Screen.Y != Size.Y)
            throw new InvalidOperationException("Stale target dimensions.");
        if (states.Length > MaxOccurrences || states.Length != occurrences.Length)
            throw new InvalidOperationException("Invalid ordered state stream.");
        if (textureRuns < segments.Length || textureRuns > states.Length)
            throw new InvalidOperationException("Invalid ordered texture-run count.");
        foreach (State state in states)
            if (state.ArtSlot >= ArtSlots) throw new InvalidOperationException("Invalid ordered art selector; use canvas.");
        _frameTextureSets.Clear();
        _usedTextureSets.Clear();
        _resolvedTextures.Clear();
        uint next = 0;
        foreach (Segment segment in segments)
        {
            if (segment.FirstVertex != next || segment.VertexCount == 0 || segment.VertexCount % 6 != 0)
                throw new InvalidOperationException("Segments must partition the original ordered triangle stream.");
            next = checked(next + segment.VertexCount);
            if (next / 6 > (uint)occurrences.Length) throw new InvalidOperationException("Out-of-range draw.");
            _frameTextureSets.Add(TextureSetRT(segment.Textures));
        }
        if (next / 6 != (uint)occurrences.Length) throw new InvalidOperationException("Incomplete command stream.");
        if (resident != null && !_depthMode) throw new InvalidOperationException("Resident chunks need the depth target.");
        Rid[] residentSets = resident == null ? Array.Empty<Rid>() : new Rid[resident.Draws.Count];
        if (resident != null)
        {
            for (int i = 0; i < resident.Draws.Count; i++)
                residentSets[i] = TextureSetRT(resident.Draws[i].Textures, _residentShader, _residentTextureSets);
            UploadResidentChunksRT(resident);
        }
        if (!occurrences.IsEmpty)
        {
            UploadResidentRT(occurrences, dynamic, states);
        }
        else
        {
            // No storage binding or zero-length BufferUpdate is needed. The
            // target still clears below. Regrowth must upload the active stream.
            _appliedStateBytes = 0;
            _statesValid = true;
        }
        ActiveStateBytes = checked((uint)states.Length * 32);
        Occurrences += states.Length;
        TextureRuns += textureRuns;
        TextureBatches += segments.Length;

        // No uploads, allocations or texture discovery inside the open draw list.
        _clear[0] = opaqueBackdrop ? Colors.Black : Colors.Transparent;
        var clearFlags = _depthMode ? RD.DrawFlags.ClearColor0 | RD.DrawFlags.ClearDepth : RD.DrawFlags.ClearColor0;
        long list = _rd.DrawListBegin(_framebuffer, clearFlags, _clear, 1.0f, 0, null, 0);
        if (list == RD.InvalidId) throw new InvalidOperationException("DrawListBegin failed.");
        try
        {
            // CUO order: resident land, loose land, resident statics, remaining categories.
            // Depth resolves overlap; LessEqual ties retain the later submission.
            Span<Push> push = stackalloc Push[1];
            for (int phase = 0; phase < 4; phase++)
            {
            if ((phase == 0 || phase == 2) && resident != null && resident.Draws.Count != 0)
            {
                _rd.DrawListBindRenderPipeline(list, _residentPipeline);
                _rd.DrawListBindUniformSet(list, _residentSet, 0);
                for (int i = 0; i < resident.Draws.Count; i++)
                {
                    var draw = resident.Draws[i];
                    if (draw.Land != (phase == 0)) continue;
                    push[0] = frame;
                    push[0].FirstVertex = draw.VisibleBase;
                    push[0].Reserved0 = unchecked((uint)cameraX);
                    push[0].Reserved1 = unchecked((uint)cameraY);
                    push[0].Reserved2 = unchecked((uint)(draw.Land ? 127 : maxGroundZ));
                    _rd.DrawListBindUniformSet(list, residentSets[i], 1);
                    _rd.DrawListSetPushConstant(list, MemoryMarshal.AsBytes(push), 64);
                    _rd.DrawListDraw(list, false, 1, draw.Count * 6);
                    ResidentDrawCalls++;
                }
            }
            if ((phase == 1 || phase == 3) && !occurrences.IsEmpty)
            {
                _rd.DrawListBindRenderPipeline(list, _pipeline);
                _rd.DrawListBindUniformSet(list, _storageSet, 0);
                for (int i = 0; i < segments.Length; i++)
                {
                    bool isLand = resident != null && segments[i].FirstVertex < landEnd * 6;
                    if (isLand != (phase == 1)) continue;
                    push[0] = frame;
                    push[0].FirstVertex = segments[i].FirstVertex;
                    push[0].Reserved0 = _depthMode ? 1u : 0u; // ADR-0044: depth from the state row
                    // Probe-only negative control: the pixel oracle must catch
                    // draw-local indexing on a later texture run.
                    if (OrderedWorldCapture.InjectStateIndexFault) push[0].FirstVertex = 0;
                    _rd.DrawListBindUniformSet(list, _frameTextureSets[i], 1);
                    _rd.DrawListSetPushConstant(list, MemoryMarshal.AsBytes(push), 64);
                    // Fourth argument is a procedural VERTEX COUNT, not an offset.
                    // Base vertex is our push constant. No index/vertex array is bound.
                    _rd.DrawListDraw(list, false, 1, segments[i].VertexCount);
                    OrderedWorldCapture.DrawCalls++;
                }
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
        if (_residentTextureSets.Count > 512)
        {
            foreach (Rid set in _residentTextureSets.Values)
                if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
            _residentTextureSets.Clear();
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
        foreach (Rid set in _residentTextureSets.Values)
            if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
        _residentTextureSets.Clear();
        _resolvedTextures.Clear();
        Free(ref _residentSet);
        Free(ref _residentQuads);
        Free(ref _residentVisible);
        _residentQuadCapacity = _residentVisibleCapacity = 0;
        Free(ref _residentPipeline);
        Free(ref _residentShader);
        Free(ref _storageSet);
        Free(ref _storage);
        Free(ref _orderStorage);
        Free(ref _stateStorage);
        _resident.InvalidateGpu();
        _statesValid = false;
        Free(ref _pipeline);
        Free(ref _shader);
        Free(ref _framebuffer);
        Free(ref _depth);
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
        _capacityBytes = _orderCapacityBytes = _stateCapacityBytes = 0;
        ActiveStateBytes = StateCapacityBytes = 0;
        Size = default;
        // Dispose Presenter on the owning thread after this transaction completes.
        // Never free atlas/hue/light RIDs borrowed through TextureGetRdTexture.
    }
}
