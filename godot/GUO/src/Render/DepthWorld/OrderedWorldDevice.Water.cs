// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): ADR-0044 + ADR-0037. Styled water inside the Depth World.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.DepthWorld;

/// <summary>One frame's water inputs, snapshotted on the game thread at capture start.</summary>
internal sealed class OrderedWorldWaterFrame
{
    internal readonly float[] Block = new float[OrderedWorldWater.Slots * 4];
    internal Rid MaskRs, RipplesRs, HueRs;
    // Strong references for the ticket's lifetime (the RS RIDs above borrow them).
    internal Texture2D Mask, Ripples;
}

internal sealed partial class OrderedWorldDevice
{
    // A second program pair: the unchanged vertex shaders with the water
    // fragment. Classic frames bind only the original pipelines.
    private Rid _waterShader, _waterPipeline, _waterResidentShader, _waterResidentPipeline;
    private Rid _waterBuffer, _waterLinear, _waterRepeat;
    private readonly Dictionary<(Rid Shader, Rid Mask, Rid Ripples, Rid Light, Rid Dummy), Rid> _waterSets = new();
    private readonly float[] _waterUpload = new float[OrderedWorldWater.Slots * 4];
    internal static long WaterFrames, WaterLitFrames;
    internal bool WaterReady => _waterPipeline.IsValid;

    internal void EnsureWaterRT(string vertexGlsl, string waterFragmentGlsl, string residentVertexGlsl)
    {
        if (_waterPipeline.IsValid) return;
        if (_rd == null || !_framebuffer.IsValid) throw new InvalidOperationException("Water before the Depth World target.");
        if (_rd.LimitGet(RD.Limit.MaxTexturesPerShaderStage) < (ulong)(ArtSlots + 2 + OrderedWorldWater.Samplers.Length)
            || _rd.LimitGet(RD.Limit.MaxSamplersPerShaderStage) < (ulong)(ArtSlots + 2 + OrderedWorldWater.Samplers.Length)
            || _rd.LimitGet(RD.Limit.MaxBoundUniformSets) < 3)
            throw new NotSupportedException("Water needs eleven sampled textures and three sets; use canvas.");
        try
        {
            _waterShader = CompileRT(vertexGlsl, waterFragmentGlsl, "guo_ordered_world_water", true);
            _waterPipeline = PipelineRT(_waterShader);
            if (_depthMode)
            {
                _waterResidentShader = CompileRT(residentVertexGlsl, waterFragmentGlsl, "guo_depth_world_resident_water", false);
                _waterResidentPipeline = PipelineRT(_waterResidentShader);
            }
            _waterBuffer = _rd.UniformBufferCreate(OrderedWorldWater.Bytes, new byte[OrderedWorldWater.Bytes]);
            Require(_waterBuffer, "water uniform buffer");
            using (var linear = new RDSamplerState
            {
                MagFilter = RD.SamplerFilter.Linear, MinFilter = RD.SamplerFilter.Linear,
                MipFilter = RD.SamplerFilter.Nearest,
                RepeatU = RD.SamplerRepeatMode.ClampToEdge, RepeatV = RD.SamplerRepeatMode.ClampToEdge,
                RepeatW = RD.SamplerRepeatMode.ClampToEdge, MinLod = 0, MaxLod = 0,
            })
                _waterLinear = _rd.SamplerCreate(linear);
            Require(_waterLinear, "water linear sampler");
            using (var repeat = new RDSamplerState
            {
                MagFilter = RD.SamplerFilter.Nearest, MinFilter = RD.SamplerFilter.Nearest,
                MipFilter = RD.SamplerFilter.Nearest,
                RepeatU = RD.SamplerRepeatMode.Repeat, RepeatV = RD.SamplerRepeatMode.Repeat,
                RepeatW = RD.SamplerRepeatMode.Repeat, MinLod = 0, MaxLod = 0,
            })
                _waterRepeat = _rd.SamplerCreate(repeat);
            Require(_waterRepeat, "water mask sampler");
        }
        catch { DisposeWaterRT(); throw; }
    }

    private Rid CompileRT(string vertex, string fragment, string name, bool validate)
    {
        using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl, SourceVertex = vertex, SourceFragment = fragment };
        using RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, true);
        if (!string.IsNullOrEmpty(spirv.CompileErrorVertex) || !string.IsNullOrEmpty(spirv.CompileErrorFragment))
            throw new InvalidOperationException(spirv.CompileErrorVertex + "\n" + spirv.CompileErrorFragment);
        if (validate) OrderedWorldShaderContract.Validate(spirv.BytecodeVertex, spirv.BytecodeFragment);
        Rid shader = _rd.ShaderCreateFromSpirV(spirv, name);
        Require(shader, name);
        return shader;
    }

    // The same fixed states InitializeRT gives the Classic pipelines.
    private Rid PipelineRT(Rid shader)
    {
        using var raster = new RDPipelineRasterizationState
        { CullMode = RD.PolygonCullMode.Disabled, Wireframe = false, DiscardPrimitives = false, EnableDepthClamp = false };
        using var samples = new RDPipelineMultisampleState
        { SampleCount = RD.TextureSamples.Samples1, EnableSampleShading = false, EnableAlphaToCoverage = false, EnableAlphaToOne = false };
        using var depth = new RDPipelineDepthStencilState
        {
            EnableDepthTest = _depthMode, EnableDepthWrite = _depthMode, EnableStencil = false,
            DepthCompareOperator = RD.CompareOperator.LessOrEqual,
        };
        using var blendAttachment = new RDPipelineColorBlendStateAttachment
        {
            EnableBlend = true,
            SrcColorBlendFactor = RD.BlendFactor.One, DstColorBlendFactor = RD.BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = RD.BlendOperation.Add,
            SrcAlphaBlendFactor = RD.BlendFactor.One, DstAlphaBlendFactor = RD.BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = RD.BlendOperation.Add,
            WriteR = true, WriteG = true, WriteB = true, WriteA = true,
        };
        using var blend = new RDPipelineColorBlendState
        { EnableLogicOp = false, Attachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { blendAttachment } };
        Rid pipeline = _rd.RenderPipelineCreate(shader, _rd.FramebufferGetFormat(_framebuffer), RD.InvalidId,
            RD.RenderPrimitive.Triangles, raster, samples, depth, blend, (RD.PipelineDynamicStateFlags)0, 0,
            new Godot.Collections.Array<RDPipelineSpecializationConstant>());
        Require(pipeline, "water pipeline");
        if (!_rd.RenderPipelineIsValid(pipeline)) throw new InvalidOperationException("Invalid water pipeline.");
        return pipeline;
    }

    /// <summary>
    /// Uploads this frame's block and returns the set-2 bindings for
    /// <paramref name="shader"/>. Light comes from this frame's speed light
    /// target when the caller rendered it before the world draw.
    /// </summary>
    private Rid WaterSetRT(OrderedWorldWaterFrame water, Rid shader, Rid dummy, Rid light)
    {
        Rid mask = water.MaskRs.IsValid ? ResolveTextureRT(water.MaskRs) : dummy;
        Rid ripples = water.RipplesRs.IsValid ? ResolveTextureRT(water.RipplesRs) : dummy;
        var key = (shader, mask, ripples, light.IsValid ? light : dummy, dummy);
        if (_waterSets.TryGetValue(key, out Rid set) && _rd.UniformSetIsValid(set)) return set;
        // Dependencies changed (a resized light target, a new mask): drop the old sets.
        if (_waterSets.Count > 8)
        {
            foreach (Rid old in _waterSets.Values) if (_rd.UniformSetIsValid(old)) _rd.FreeRid(old);
            _waterSets.Clear();
        }
        using var block = new RDUniform { UniformType = RD.UniformType.UniformBuffer, Binding = 0 };
        block.AddId(_waterBuffer);
        using var m = Sampled(1, _waterRepeat, mask);
        using var r = Sampled(2, _waterLinear, ripples);
        using var l = Sampled(3, _waterLinear, key.Item4);
        using var s = Sampled(4, _sampler, dummy);
        using var c = Sampled(5, _sampler, dummy);
        set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { block, m, r, l, s, c }, shader, 2);
        Require(set, "water set");
        _waterSets[key] = set;
        return set;
    }

    private static RDUniform Sampled(int binding, Rid sampler, Rid texture)
    {
        var u = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = binding };
        u.AddId(sampler);
        u.AddId(texture);
        return u;
    }

    /// <summary>Before the draw list: the block with this frame's light binding, and both sets.</summary>
    private void PrepareWaterRT(OrderedWorldWaterFrame water, Vector3 ambient, bool lightRendered, out Rid orderedSet, out Rid residentSet)
    {
        Rid dummy = ResolveTextureRT(water.HueRs);
        bool lit = lightRendered && SpeedLightReady && SpeedLightPublishedLive;
        Rid light = lit ? SpeedLightPublishedRid : default;
        Array.Copy(water.Block, _waterUpload, _waterUpload.Length);
        // ADR-0048 water light: same target pixels as this world target.
        OrderedWorldWater.Set(_waterUpload, "water_has_light_target", lit ? 1f : 0f);
        OrderedWorldWater.Set(_waterUpload, "water_light_bounds", 0, 0, Size.X, Size.Y);
        OrderedWorldWater.Set(_waterUpload, "water_light_ambient", ambient.X, ambient.Y, ambient.Z);
        // Geometric reflections read the finished scene colour; the world
        // target cannot sample itself. Not yet on this path (ADR-0044 water).
        OrderedWorldWater.Set(_waterUpload, "water_has_reflections", 0f);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_waterUpload.AsSpan());
        Error error = _rd.BufferUpdate(_waterBuffer, 0, (uint)bytes.Length, bytes);
        if (error != Error.Ok) throw new InvalidOperationException("Water BufferUpdate: " + error);
        orderedSet = WaterSetRT(water, _waterShader, dummy, light);
        residentSet = _waterResidentShader.IsValid ? WaterSetRT(water, _waterResidentShader, dummy, light) : default;
        WaterFrames++;
        if (lit) WaterLitFrames++;
    }

    private void DisposeWaterRT()
    {
        if (_rd == null) return;
        foreach (Rid set in _waterSets.Values) if (_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
        _waterSets.Clear();
        Free(ref _waterResidentPipeline);
        Free(ref _waterResidentShader);
        Free(ref _waterPipeline);
        Free(ref _waterShader);
        Free(ref _waterBuffer);
        Free(ref _waterLinear);
        Free(ref _waterRepeat);
    }
}
