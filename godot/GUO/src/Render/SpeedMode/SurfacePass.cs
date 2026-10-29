// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer.SpeedMode;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.DepthWorld;

// ADR-0048: second pipelines only. All methods run on the rendering thread.
internal sealed partial class OrderedWorldDevice
{
    private Rid _surfaceColor, _surfaceDepth, _surfaceFramebuffer;
    private Rid _surfaceShader, _surfaceResidentShader, _surfacePipeline, _surfaceResidentPipeline;
    private Rid _surfaceRows, _surfaceSet, _surfaceResidentSet;
    private uint _surfaceRowCapacity;
    private Rid _surfaceGeometry, _surfaceOrder, _surfaceStates, _surfaceResidentGeometry, _surfaceResidentOrder;
    private readonly Dictionary<TextureSet, Rid> _surfaceTextures = new(), _surfaceResidentTextures = new();
    internal Texture2Drd SurfaceTexture { get; } = new();
    private Rid[] _surfaceDynamicSets = Array.Empty<Rid>(), _surfaceResidentSets = Array.Empty<Rid>();
    internal static long SurfaceDrawCalls;
    internal bool SurfaceReady { get; private set; }
    internal bool HasSurfaceResources => _surfaceColor.IsValid || _surfaceShader.IsValid || _surfaceRows.IsValid;

    internal static string SpeedShader(string name)
    {
        using var stream = typeof(OrderedWorldDevice).Assembly.GetManifestResourceStream("speedmode/" + name)
            ?? throw new InvalidOperationException("Missing lighting speed shader: " + name);
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Rid SurfaceShaderRT(string vertex)
    {
        using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl,
            SourceVertex = SpeedShader(vertex), SourceFragment = SpeedShader("surface_fragment.glsl.txt") };
        using var code = _rd.ShaderCompileSpirVFromSource(source);
        if (!string.IsNullOrEmpty(code.CompileErrorVertex) || !string.IsNullOrEmpty(code.CompileErrorFragment))
            throw new InvalidOperationException(code.CompileErrorVertex + code.CompileErrorFragment);
        var shader = _rd.ShaderCreateFromSpirV(code, "guo_speed_surface");
        Require(shader, "surface shader");
        return shader;
    }

    private Rid SurfacePipelineRT(Rid shader)
    {
        using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
        using var samples = new RDPipelineMultisampleState();
        using var depth = new RDPipelineDepthStencilState { EnableDepthTest = true, EnableDepthWrite = true,
            DepthCompareOperator = RD.CompareOperator.LessOrEqual };
        using var attachment = new RDPipelineColorBlendStateAttachment { EnableBlend = false,
            WriteR = true, WriteG = true, WriteB = true, WriteA = true };
        using var blend = new RDPipelineColorBlendState {
            Attachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
        var pipeline = _rd.RenderPipelineCreate(shader, _rd.FramebufferGetFormat(_surfaceFramebuffer), RD.InvalidId,
            RD.RenderPrimitive.Triangles, raster, samples, depth, blend);
        Require(pipeline, "surface pipeline");
        return pipeline;
    }

    private void EnsureSurfaceRT()
    {
        if (_surfaceFramebuffer.IsValid) return;
        if (!_depthMode) throw new NotSupportedException("Lighting speed mode requires the Depth World.");
        var usage = RD.TextureUsageBits.ColorAttachmentBit | RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit;
        if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.R16G16B16A16Sfloat, usage))
            throw new NotSupportedException("Lighting speed mode requires RGBA16F attachments.");
        using var format = new RDTextureFormat { Width = (uint)Size.X, Height = (uint)Size.Y,
            Depth = 1, ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D,
            Format = RD.DataFormat.R16G16B16A16Sfloat, UsageBits = usage };
        using var view = new RDTextureView();
        _surfaceColor = _rd.TextureCreate(format, view);
        Require(_surfaceColor, "surface texture");
        format.Format = RD.DataFormat.D32Sfloat;
        format.UsageBits = RD.TextureUsageBits.DepthStencilAttachmentBit;
        _surfaceDepth = _rd.TextureCreate(format, view);
        Require(_surfaceDepth, "surface depth");
        _surfaceFramebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _surfaceColor, _surfaceDepth });
        Require(_surfaceFramebuffer, "surface framebuffer");
        _surfaceShader = SurfaceShaderRT("surface_dynamic.glsl.txt");
        _surfaceResidentShader = SurfaceShaderRT("surface_resident.glsl.txt");
        _surfacePipeline = SurfacePipelineRT(_surfaceShader);
        _surfaceResidentPipeline = SurfacePipelineRT(_surfaceResidentShader);
        SurfaceTexture.TextureRdRid = _surfaceColor;
    }

    private Rid SurfaceStorageSetRT(Rid shader, params Rid[] buffers)
    {
        var uniforms = new Godot.Collections.Array<RDUniform>();
        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                var uniform = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = i };
                uniform.AddId(buffers[i]); uniforms.Add(uniform);
            }
            var set = _rd.UniformSetCreate(uniforms, shader, 0);
            Require(set, "surface storage set"); return set;
        }
        finally { foreach (var u in uniforms) u.Dispose(); }
    }

    private Rid SurfaceTexturesRT(TextureSet textures, bool resident)
    {
        var cache = resident ? _surfaceResidentTextures : _surfaceTextures;
        if (!_resolvedTextures.TryGetValue(textures,out var resolved))
            throw new InvalidOperationException("Surface run has no matching colour texture transaction.");
        var key = new TextureSet(resolved.Art0,resolved.Art1,resolved.Art2,resolved.Art3,default,default);
        if (cache.TryGetValue(key, out var existing) && _rd.UniformSetIsValid(existing)) return existing;
        using var a = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 }, key.Art0);
        using var b = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 1 }, key.Art1);
        using var c = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 2 }, key.Art2);
        using var d = SamplerUniform(new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 3 }, key.Art3);
        var set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { a, b, c, d },
            resident ? _surfaceResidentShader : _surfaceShader, 1);
        Require(set, "surface texture set"); cache[key] = set; return set;
    }

    internal void RenderSurfaceRT(ReadOnlySpan<SurfaceRow> rows, ReadOnlySpan<Segment> segments, in Push frame,
        DepthWorldResident resident, int cameraX, int cameraY, int maxGroundZ, int landEnd)
    {
        SurfaceReady = false;
        try
        {
            EnsureSurfaceRT();
            if (!rows.IsEmpty)
            {
                uint bytes = checked((uint)rows.Length * 8);
                if (bytes > _surfaceRowCapacity)
                { Free(ref _surfaceSet); GrowBuffer(ref _surfaceRows, ref _surfaceRowCapacity, bytes); }
                Upload(_surfaceRows, MemoryMarshal.AsBytes(rows), 0);
                if (_surfaceGeometry != _storage || _surfaceOrder != _orderStorage || _surfaceStates != _stateStorage)
                    Free(ref _surfaceSet);
                if (!_surfaceSet.IsValid)
                {
                    _surfaceSet = SurfaceStorageSetRT(_surfaceShader, _storage, _orderStorage, _stateStorage, _surfaceRows);
                    _surfaceGeometry = _storage; _surfaceOrder = _orderStorage; _surfaceStates = _stateStorage;
                }
            }
            if (resident != null && resident.Draws.Count > 0)
            {
                if (_surfaceResidentGeometry != _residentQuads || _surfaceResidentOrder != _residentVisible)
                    Free(ref _surfaceResidentSet);
                if (!_surfaceResidentSet.IsValid)
                {
                    _surfaceResidentSet = SurfaceStorageSetRT(_surfaceResidentShader, _residentQuads, _residentVisible);
                    _surfaceResidentGeometry = _residentQuads; _surfaceResidentOrder = _residentVisible;
                }
            }
            // Descriptor sets borrow art views. Bound growth during long walks,
            // using the same deferred-free lifetime as the colour device cache.
            TrimSurfaceCacheRT(_surfaceTextures);
            TrimSurfaceCacheRT(_surfaceResidentTextures);
            // Resolve all borrowed views before opening the draw list.
            if (_surfaceDynamicSets.Length < segments.Length) Array.Resize(ref _surfaceDynamicSets, Math.Max(16,segments.Length * 2));
            for (int i=0;i<segments.Length;i++) _surfaceDynamicSets[i]=SurfaceTexturesRT(segments[i].Textures,false);
            int residentCount = resident?.Draws.Count ?? 0;
            if (_surfaceResidentSets.Length < residentCount) Array.Resize(ref _surfaceResidentSets,Math.Max(16,residentCount * 2));
            for (int i=0;i<residentCount;i++) _surfaceResidentSets[i]=SurfaceTexturesRT(resident.Draws[i].Textures,true);
            _clear[0] = Colors.Transparent;
            long list = _rd.DrawListBegin(_surfaceFramebuffer, RD.DrawFlags.ClearColor0 | RD.DrawFlags.ClearDepth, _clear, 1.0f);
            if (list == RD.InvalidId) throw new InvalidOperationException("Surface DrawListBegin failed.");
            try
            {
                Span<Push> push = stackalloc Push[1];
                for (int phase = 0; phase < 4; phase++)
                {
                    if ((phase == 0 || phase == 2) && resident != null && resident.Draws.Count > 0)
                    {
                        _rd.DrawListBindRenderPipeline(list, _surfaceResidentPipeline);
                        _rd.DrawListBindUniformSet(list, _surfaceResidentSet, 0);
                        for (int i=0;i<resident.Draws.Count;i++)
                        {
                            var draw=resident.Draws[i];
                            if (draw.Land != (phase == 0)) continue;
                            push[0] = frame; push[0].FirstVertex = draw.VisibleBase;
                            push[0].Reserved0 = unchecked((uint)cameraX); push[0].Reserved1 = unchecked((uint)cameraY);
                            push[0].Reserved2 = unchecked((uint)(draw.Land ? 127 : maxGroundZ));
                            _rd.DrawListBindUniformSet(list, _surfaceResidentSets[i], 1);
                            _rd.DrawListSetPushConstant(list, MemoryMarshal.AsBytes(push), 64);
                            _rd.DrawListDraw(list, false, 1, draw.Count * 6); SurfaceDrawCalls++;
                        }
                    }
                    if ((phase == 1 || phase == 3) && !rows.IsEmpty)
                    {
                        _rd.DrawListBindRenderPipeline(list, _surfacePipeline);
                        _rd.DrawListBindUniformSet(list, _surfaceSet, 0);
                        for (int i=0;i<segments.Length;i++)
                        {
                            var segment=segments[i];
                            bool land = resident != null && segment.FirstVertex < landEnd * 6;
                            if (land != (phase == 1)) continue;
                            push[0] = frame; push[0].FirstVertex = segment.FirstVertex;
                            push[0].Reserved0 = 1; push[0].Reserved1 = unchecked((uint)cameraX);
                            push[0].Reserved2 = unchecked((uint)cameraY);
                            _rd.DrawListBindUniformSet(list, _surfaceDynamicSets[i], 1);
                            _rd.DrawListSetPushConstant(list, MemoryMarshal.AsBytes(push), 64);
                            _rd.DrawListDraw(list, false, 1, segment.VertexCount); SurfaceDrawCalls++;
                        }
                    }
                }
            }
            finally { _rd.DrawListEnd(); }
            SurfaceReady = true;
        }
        catch { DisposeSurfaceRT(); throw; }
    }

    private void TrimSurfaceCacheRT(Dictionary<TextureSet,Rid> cache)
    {
        if (cache.Count <= 512) return;
        foreach (var set in cache.Values) if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
        cache.Clear();
    }

    internal void DisableSurfaceRT() { SurfaceReady = false; if (_surfaceColor.IsValid) DisposeSurfaceRT(); }
    internal byte[] ReadColorRT() => _rd.TextureGetData(_color, 0);
    internal byte[] ReadSurfaceRT() => _rd.TextureGetData(_surfaceColor, 0);
    private void DisposeSurfaceRT()
    {
        SurfaceReady = false;
        SurfaceTexture.TextureRdRid = default;
        if (_rd == null) return;
        foreach (var cache in new[] { _surfaceTextures, _surfaceResidentTextures })
        { foreach (var set in cache.Values) if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set); cache.Clear(); }
        Free(ref _surfaceSet); Free(ref _surfaceResidentSet); Free(ref _surfaceRows); _surfaceRowCapacity = 0;
        Free(ref _surfacePipeline); Free(ref _surfaceResidentPipeline);
        Free(ref _surfaceShader); Free(ref _surfaceResidentShader);
        Free(ref _surfaceFramebuffer); Free(ref _surfaceDepth); Free(ref _surfaceColor);
        _surfaceGeometry = _surfaceOrder = _surfaceStates = _surfaceResidentGeometry = _surfaceResidentOrder = default;
    }
}
