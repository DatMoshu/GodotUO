// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.DepthWorld;

internal sealed partial class OrderedWorldDevice
{
    private LightPresentationPassRd _lightPresentation;

    private Rid PresentLightRT(Rid source)
    {
        _lightPresentation ??= new LightPresentationPassRd();
        return _lightPresentation.Render(_rd, Size, source);
    }

    private void DisposeLightPresentationRT()
    {
        _lightPresentation?.Dispose();
        _lightPresentation = null;
    }
}

/// <summary>
/// Render-thread-only final transfer, matching RealtimeLightTarget's max-channel
/// normalization. The raw HDR accumulation remains available for diagnostics.
/// One nearest RGBA16F target and one full-screen draw; no art resampling.
/// </summary>
internal sealed class LightPresentationPassRd : IDisposable
{
    private RD _rd;
    private Rid _target, _framebuffer, _shader, _pipeline, _sampler, _set, _source;
    private Vector2I _size;
    internal Rid Output => _target;

    internal Rid Render(RD device, Vector2I size, Rid source)
    {
        try
        {
            _rd ??= device ?? throw new NotSupportedException("Light presentation requires a RenderingDevice.");
            if (_rd != device || size.X <= 0 || size.Y <= 0 || !source.IsValid || !_rd.TextureIsValid(source)
                || source == _target)
                throw new InvalidOperationException("Light presentation has no valid distinct source.");
            EnsureResources(size);
            if (!_set.IsValid || !_rd.UniformSetIsValid(_set) || _source != source)
            {
                FreeSet();
                using var input = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 };
                input.AddId(_sampler); input.AddId(source);
                _set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { input }, _shader, 0);
                Require(_set);
                _source = source;
            }
            _rd.CaptureTimestamp("guo_light_presentation_begin");
            long list = _rd.DrawListBegin(_framebuffer);
            if (list == RD.InvalidId) throw new InvalidOperationException("Cannot open light presentation draw list.");
            try
            {
                _rd.DrawListBindRenderPipeline(list, _pipeline);
                _rd.DrawListBindUniformSet(list, _set, 0);
                _rd.DrawListDraw(list, false, 1, 3);
            }
            finally { _rd.DrawListEnd(); }
            _rd.CaptureTimestamp("guo_light_presentation_end");
            return _target;
        }
        catch { Dispose(); throw; }
    }

    private void EnsureResources(Vector2I size)
    {
        if (!_shader.IsValid)
        {
            using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl,
                SourceVertex = OrderedWorldDevice.SpeedShader("soften_vertex.glsl.txt"),
                SourceFragment = OrderedWorldDevice.SpeedShader("light_present_fragment.glsl.txt") };
            using var code = _rd.ShaderCompileSpirVFromSource(source);
            if (!string.IsNullOrEmpty(code.CompileErrorVertex) || !string.IsNullOrEmpty(code.CompileErrorFragment))
                throw new InvalidOperationException(code.CompileErrorVertex + code.CompileErrorFragment);
            _shader = _rd.ShaderCreateFromSpirV(code, "guo_light_presentation"); Require(_shader);
            using var sampler = new RDSamplerState { MinFilter = RD.SamplerFilter.Nearest,
                MagFilter = RD.SamplerFilter.Nearest, MipFilter = RD.SamplerFilter.Nearest,
                RepeatU = RD.SamplerRepeatMode.ClampToEdge, RepeatV = RD.SamplerRepeatMode.ClampToEdge,
                MinLod = 0, MaxLod = 0 };
            _sampler = _rd.SamplerCreate(sampler); Require(_sampler);
        }
        if (_target.IsValid && _size == size) return;
        FreeSet(); Free(ref _pipeline); Free(ref _framebuffer); Free(ref _target);
        using var format = new RDTextureFormat { Width = (uint)size.X, Height = (uint)size.Y, Depth = 1,
            ArrayLayers = 1, Mipmaps = 1, TextureType = RD.TextureType.Type2D, Format = RD.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RD.TextureUsageBits.ColorAttachmentBit | RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit };
        using var view = new RDTextureView();
        _target = _rd.TextureCreate(format, view); Require(_target);
        _framebuffer = _rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _target }); Require(_framebuffer);
        using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
        using var samples = new RDPipelineMultisampleState();
        using var depth = new RDPipelineDepthStencilState();
        using var attachment = new RDPipelineColorBlendStateAttachment { WriteR = true, WriteG = true, WriteB = true, WriteA = true };
        using var blend = new RDPipelineColorBlendState { Attachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
        _pipeline = _rd.RenderPipelineCreate(_shader, _rd.FramebufferGetFormat(_framebuffer), RD.InvalidId,
            RD.RenderPrimitive.Triangles, raster, samples, depth, blend); Require(_pipeline);
        _size = size;
    }

    private static void Require(Rid resource)
    {
        if (!resource.IsValid) throw new InvalidOperationException("Cannot allocate light presentation resources.");
    }
    private void Free(ref Rid resource)
    {
        if (resource.IsValid) _rd.FreeRid(resource);
        resource = default;
    }
    private void FreeSet()
    {
        if (_set.IsValid && _rd.UniformSetIsValid(_set)) _rd.FreeRid(_set);
        _set = _source = default;
    }
    public void Dispose()
    {
        if (_rd == null) return;
        FreeSet(); Free(ref _pipeline); Free(ref _shader); Free(ref _sampler);
        Free(ref _framebuffer); Free(ref _target);
        _rd = null; _size = default;
    }
}
