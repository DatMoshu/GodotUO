// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048: additive analytic window patches on the surface buffer.
using System;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;
using N2 = System.Numerics.Vector2;

namespace GUO.Renderer.SpeedMode.WindowLight;

internal sealed class WindowLightPassRd : IDisposable
{
    private RD _rd;
    private Rid _shader, _pipeline, _buffer, _set, _surfaceSet, _sampler, _surface;
    private long _format = -1;
    private uint _capacity;
    public string Failure { get; private set; }

    // Render-thread only. Framebuffer is the core's HDR light target, already
    // cleared to ambient; this pass loads and adds to it. Nothing is created for
    // zero patches. origin restores the per-frame-relative surface buffer XY.
    public bool Draw(RD device,Rid framebuffer,Rid surface,N2 origin,int width,int height,
        ReadOnlySpan<WindowLightPatch> patches)
    {
        if (patches.IsEmpty) return true;
        if (Failure != null) return false;
        try
        {
            if (patches.Length > 4096 || width <= 0 || height <= 0) throw new InvalidOperationException("Window patch capacity or target size is invalid.");
            _rd ??= device ?? throw new NotSupportedException("Window light needs the Depth World RenderingDevice.");
            if (_rd != device) throw new InvalidOperationException("Window light device changed.");
            Ensure(framebuffer);
            uint bytes = (uint)(patches.Length*WindowLightPatch.Bytes);
            if (_capacity < bytes)
            {
                Free(ref _set); Free(ref _buffer);
                _capacity = Math.Max(4096,_capacity);
                while (_capacity < bytes) _capacity *= 2;
                _buffer = _rd.StorageBufferCreate(_capacity,ReadOnlySpan<byte>.Empty,(RD.StorageBufferUsage)0,(RD.BufferCreationBits)0);
                Require(_buffer,"window patch buffer");
                using var u = new RDUniform { UniformType = RD.UniformType.StorageBuffer, Binding = 0 };
                u.AddId(_buffer);
                _set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u },_shader,0);
                Require(_set,"window patch set");
            }
            if (_rd.BufferUpdate(_buffer,0,bytes,MemoryMarshal.AsBytes(patches)) != Error.Ok)
                throw new InvalidOperationException("Window patch upload failed.");
            if (_surface != surface || !_surfaceSet.IsValid || !_rd.UniformSetIsValid(_surfaceSet))
            {
                Free(ref _surfaceSet);
                using var u = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 };
                u.AddId(_sampler); u.AddId(surface);
                _surfaceSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u },_shader,1);
                Require(_surfaceSet,"window surface set");
                _surface = surface;
            }
            _rd.CaptureTimestamp("speed.window-light.begin");
            long list = _rd.DrawListBegin(framebuffer,(RD.DrawFlags)0,Array.Empty<Godot.Color>(),1,0,null,0);
            if (list == RD.InvalidId) throw new InvalidOperationException("Window light draw list failed.");
            try
            {
                _rd.DrawListBindRenderPipeline(list,_pipeline);
                _rd.DrawListBindUniformSet(list,_set,0);
                _rd.DrawListBindUniformSet(list,_surfaceSet,1);
                Span<float> push = stackalloc float[] { width,height,origin.X,origin.Y };
                _rd.DrawListSetPushConstant(list,MemoryMarshal.AsBytes(push),16);
                _rd.DrawListDraw(list,false,(uint)patches.Length,6);
            }
            finally { _rd.DrawListEnd(); }
            _rd.CaptureTimestamp("speed.window-light.end");
            return true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Dispose();
            GD.PrintErr("[speed-window-light] Window patches disabled: " + Failure);
            return false;
        }
    }

    private void Ensure(Rid framebuffer)
    {
        if (!_shader.IsValid)
        {
            using var source = new RDShaderSource { Language = RD.ShaderLanguage.Glsl,
                SourceVertex = VertexGlsl, SourceFragment = FragmentGlsl };
            using RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source,true);
            if (!string.IsNullOrEmpty(spirv.CompileErrorVertex) || !string.IsNullOrEmpty(spirv.CompileErrorFragment))
                throw new InvalidOperationException(spirv.CompileErrorVertex+spirv.CompileErrorFragment);
            _shader = _rd.ShaderCreateFromSpirV(spirv,"guo_window_light");
            Require(_shader,"window light shader");
            using var sampler = new RDSamplerState { MagFilter = RD.SamplerFilter.Nearest, MinFilter = RD.SamplerFilter.Nearest,
                MipFilter = RD.SamplerFilter.Nearest, RepeatU = RD.SamplerRepeatMode.ClampToEdge,
                RepeatV = RD.SamplerRepeatMode.ClampToEdge, MaxLod = 0 };
            _sampler = _rd.SamplerCreate(sampler);
            Require(_sampler,"nearest surface sampler");
        }
        long format = _rd.FramebufferGetFormat(framebuffer);
        if (_pipeline.IsValid && format == _format) return;
        Free(ref _pipeline);
        using var raster = new RDPipelineRasterizationState { CullMode = RD.PolygonCullMode.Disabled };
        using var samples = new RDPipelineMultisampleState { SampleCount = RD.TextureSamples.Samples1 };
        using var depth = new RDPipelineDepthStencilState { EnableDepthTest = false, EnableDepthWrite = false };
        using var attachment = new RDPipelineColorBlendStateAttachment { EnableBlend = true,
            SrcColorBlendFactor = RD.BlendFactor.One, DstColorBlendFactor = RD.BlendFactor.One, ColorBlendOp = RD.BlendOperation.Add,
            SrcAlphaBlendFactor = RD.BlendFactor.One, DstAlphaBlendFactor = RD.BlendFactor.One, AlphaBlendOp = RD.BlendOperation.Add,
            WriteR = true, WriteG = true, WriteB = true, WriteA = false };
        using var blend = new RDPipelineColorBlendState { Attachments = new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
        _pipeline = _rd.RenderPipelineCreate(_shader,format,RD.InvalidId,RD.RenderPrimitive.Triangles,
            raster,samples,depth,blend,(RD.PipelineDynamicStateFlags)0,0,new Godot.Collections.Array<RDPipelineSpecializationConstant>());
        Require(_pipeline,"additive window pipeline");
        _format = format;
    }

    private static void Require(Rid rid,string name) { if (!rid.IsValid) throw new InvalidOperationException("Could not create "+name); }
    private void Free(ref Rid rid) { if (rid.IsValid) _rd?.FreeRid(rid); rid = default; }
    public void Dispose()
    {
        Free(ref _surfaceSet); Free(ref _set); Free(ref _buffer); _capacity = 0;
        Free(ref _pipeline); Free(ref _sampler); Free(ref _shader); _format = -1;
        _surface = default;
    }

    private const string CommonGlsl = """
        struct Patch { vec4 origin; vec4 basis; vec4 opening; vec4 screen; vec4 colour; vec4 rows[4]; };
        layout(set=0,binding=0,std430) readonly buffer Patches { Patch patches[]; };
        layout(push_constant,std430) uniform Frame { vec4 screen_origin; } pc;
        """;
    private const string VertexGlsl = "#version 450\n"+CommonGlsl+"""
        layout(location=0) flat out uint patch_index;
        void main() {
            const vec2 corners[6] = vec2[](vec2(0,0),vec2(1,0),vec2(0,1),vec2(1,0),vec2(1,1),vec2(0,1));
            patch_index = uint(gl_InstanceIndex);
            Patch p = patches[patch_index];
            vec2 screen = mix(p.screen.xy,p.screen.zw,corners[gl_VertexIndex]);
            gl_Position = vec4(screen*2.0/pc.screen_origin.xy-1.0,0,1);
        }
        """;
    private const string FragmentGlsl = "#version 450\n"+CommonGlsl+"""
        layout(set=1,binding=0) uniform sampler2D surface_buffer;
        layout(location=0) flat in uint patch_index;
        layout(location=0) out vec4 light;
        void main() {
            Patch p = patches[patch_index];
            vec4 surface = texelFetch(surface_buffer,ivec2(gl_FragCoord.xy),0);
            int surface_flags = int(round(surface.w));
            int cls = surface_flags%16;
            bool covered = ((surface_flags/16)&1) != 0;
            if (!covered || (cls != 1 && cls != 4) || surface.z < p.origin.z-1.0 ||
                surface.z > p.origin.z+(cls == 4 ? p.opening.w : 1.0)) discard;
            vec2 delta = surface.xy+pc.screen_origin.zw-p.origin.xy;
            float det = p.basis.x*p.basis.w-p.basis.y*p.basis.z;
            if (abs(det) < 0.000001) discard;
            vec2 uv = vec2(delta.x*p.basis.w-delta.y*p.basis.z,
                p.basis.x*delta.y-p.basis.y*delta.x)/det;
            if (any(lessThanEqual(uv,vec2(0))) || any(greaterThanEqual(uv,vec2(1)))) discard;
            int shape = int(p.opening.x);
            float x = uv.x*2.0-1.0;
            float spring = max(0.0,1.0-p.opening.y*0.5);
            if (shape == 0) discard;
            if (shape == 2 && uv.y > spring && x*x+pow((uv.y-spring)/(1.0-spring),2.0) > 1.0) discard;
            if (shape == 3 && uv.y > 0.5 && pow(abs(x)+1.0,2.0)+3.0*pow(2.0*uv.y-1.0,2.0) > 4.0) discard;
            if (shape == 4 && x*x+pow(2.0*uv.y-1.0,2.0) > 1.0) discard;
            int count = int(p.opening.z);
            float rowpos = (1.0-uv.y)*float(count);
            int row = min(count-1,int(rowpos));
            float row_edge = min(fract(rowpos),1.0-fract(rowpos))/float(count);
            if (row_edge < 0.0175 && rowpos > 0.0175*float(count) && rowpos < float(count)-0.0175*float(count)) discard;
            int panes = int(p.rows[row/4][row%4]);
            float column = uv.x*float(panes);
            float col_edge = min(fract(column),1.0-fract(column))/float(panes);
            if (col_edge < 0.0175 && column > 0.0175*float(panes) && column < float(panes)-0.0175*float(panes)) discard;
            light = vec4(p.colour.rgb*p.origin.w,0);
        }
        """;
}
