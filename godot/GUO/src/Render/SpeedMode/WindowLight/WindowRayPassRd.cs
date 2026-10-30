// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048: bounded screen-space receiver shafts; no volumetric/GI claim.
using System;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;
using N2 = System.Numerics.Vector2;

namespace GUO.Renderer.SpeedMode.WindowLight;

internal sealed class WindowRayPassRd : IDisposable
{
    private RD _rd;
    private Rid _shader, _pipeline, _buffer, _set, _surfaceSet, _sampler, _surface, _sun;
    private long _format = -1;
    private uint _capacity;
    public string Failure { get; private set; }
    internal uint BufferBytes=>_capacity;
    internal int ResourceCount=>(_shader.IsValid?1:0)+(_pipeline.IsValid?1:0)+(_buffer.IsValid?1:0)
        +(_set.IsValid?1:0)+(_surfaceSet.IsValid?1:0)+(_sampler.IsValid?1:0);

    // Render-thread only. Framebuffer is the core's HDR light target, already
    // cleared to ambient; this pass loads and adds to it. Nothing is created for
    // zero patches. origin restores the per-frame-relative surface buffer XY.
    public bool Draw(RD device,Rid framebuffer,Rid surface,Rid sun,N2 origin,int width,int height,
        ReadOnlySpan<WindowRayPatch> patches)
    {
        if (patches.IsEmpty) return true;
        if (Failure != null) return false;
        try
        {
            if (patches.Length > WindowRayPatch.Limit || width <= 0 || height <= 0) throw new InvalidOperationException("Window patch capacity or target size is invalid.");
            _rd ??= device ?? throw new NotSupportedException("Window light needs the Depth World RenderingDevice.");
            if (_rd != device) throw new InvalidOperationException("Window light device changed.");
            if(!framebuffer.IsValid || !surface.IsValid || !sun.IsValid)
                throw new InvalidOperationException("Window shafts require current surface and sun-mask views.");
            Ensure(framebuffer);
            uint bytes = (uint)(patches.Length*WindowRayPatch.Bytes);
            if (_capacity < bytes)
            {
                FreeSet(ref _set); Free(ref _buffer);
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
            if (_surface != surface || _sun != sun || !_surfaceSet.IsValid || !_rd.UniformSetIsValid(_surfaceSet))
            {
                FreeSet(ref _surfaceSet);
                using var u = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 0 };
                u.AddId(_sampler); u.AddId(surface);
                using var occlusion = new RDUniform { UniformType = RD.UniformType.SamplerWithTexture, Binding = 1 };
                occlusion.AddId(_sampler); occlusion.AddId(sun);
                _surfaceSet = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u,occlusion },_shader,1);
                Require(_surfaceSet,"window surface set");
                _surface = surface; _sun=sun;
            }
            _rd.CaptureTimestamp("speed.window-rays.begin");
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
            _rd.CaptureTimestamp("speed.window-rays.end");
            return true;
        }
        catch (Exception error)
        {
            Failure = error.Message;
            Dispose();
            GD.PrintErr("[speed-window-rays] Receiver shafts disabled: " + Failure);
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
            _shader = _rd.ShaderCreateFromSpirV(spirv,"guo_window_rays");
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
    private void FreeSet(ref Rid rid) { if(rid.IsValid && _rd?.UniformSetIsValid(rid)==true) _rd.FreeRid(rid);rid=default; }
    public void Dispose()
    {
        FreeSet(ref _surfaceSet); FreeSet(ref _set); Free(ref _buffer); _capacity = 0;
        Free(ref _pipeline); Free(ref _sampler); Free(ref _shader); _format = -1;
        _surface = _sun = default;
    }

    private const string CommonGlsl = """
        struct Patch { vec4 origin; vec4 basis; vec4 opening; vec4 screen; vec4 colour; vec4 rows[4]; };
        struct Ray { Patch window; vec4 source; };
        layout(set=0,binding=0,std430) readonly buffer Patches { Ray rays[]; };
        layout(push_constant,std430) uniform Frame { vec4 screen_origin; } pc;
        """;
    private const string VertexGlsl = "#version 450\n"+CommonGlsl+"""
        layout(location=0) flat out uint patch_index;
        void main() {
            const vec2 corners[6] = vec2[](vec2(0,0),vec2(1,0),vec2(0,1),vec2(1,0),vec2(1,1),vec2(0,1));
            patch_index = uint(gl_InstanceIndex);
            Patch p = rays[patch_index].window;
            vec2 screen = mix(p.screen.xy,p.screen.zw,corners[gl_VertexIndex]);
            gl_Position = vec4(screen*2.0/pc.screen_origin.xy-1.0,0,1);
        }
        """;
    private const string FragmentGlsl = "#version 450\n"+CommonGlsl+"""
        layout(set=1,binding=0) uniform sampler2D surface_buffer;
        layout(set=1,binding=1) uniform sampler2D sun_mask;
        layout(location=0) flat in uint patch_index;
        layout(location=0) out vec4 light;
        void main() {
            Patch p = rays[patch_index].window;
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
            // Current visible receivers only. This cannot see hidden geometry;
            // reject ambiguous screen-space paths instead of inventing volume.
            for(int i=0;i<8;i++) {
                float t=float(i+1)/9.0;
                vec2 pixel=mix(rays[patch_index].source.xy,gl_FragCoord.xy,t);
                if(any(lessThan(pixel,vec2(0))) || any(greaterThanEqual(pixel,pc.screen_origin.xy))) discard;
                vec4 hit=texelFetch(surface_buffer,ivec2(pixel),0);
                int flags=int(round(hit.w)), kind=flags%16;
                if(((flags/16)&1)==0 || (kind!=1 && kind!=4) || hit.z<p.origin.z-1.0 ||
                    hit.z>p.origin.z+(kind==4 ? 4.0 : 1.0)) discard;
                if(texelFetch(sun_mask,ivec2(pixel),0).r>0.08) discard;
            }
            if(texelFetch(sun_mask,ivec2(gl_FragCoord.xy),0).r>0.08) discard;
            int panes=max(1,int(p.rows[0].x));
            float centre=1.0-abs(fract(uv.x*float(panes))*2.0-1.0);
            float shaft=pow(max(0.0,centre),4.0)*smoothstep(0.0,0.12,uv.y)*pow(1.0-uv.y,2.0);
            // At most 16 records * .04 per channel; no temporal accumulation.
            light=vec4(clamp(p.colour.rgb,vec3(0),vec3(1))*p.origin.w*0.04*shaft,0);

        }
        """;
}
