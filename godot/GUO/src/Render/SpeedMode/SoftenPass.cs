// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer.SpeedMode;
using RD=Godot.RenderingDevice;

namespace GUO.Renderer.DepthWorld;

internal sealed partial class OrderedWorldDevice
{
    private Rid _softenA,_softenB,_softenFramebufferA,_softenFramebufferB;
    private Rid _softenShader,_softenPipeline,_softenSetA,_softenSetB,_speedOutput;
    [StructLayout(LayoutKind.Sequential)]
    private struct SoftenPush { internal Vector4 ScreenDirection; internal int Steps,Pad0,Pad1,Pad2; }
    private void EnsureSoftenRT()
    {
        if(_softenA.IsValid) return;
        using var format=new RDTextureFormat { Width=(uint)Size.X,Height=(uint)Size.Y,Depth=1,
            ArrayLayers=1,Mipmaps=1,TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R16G16B16A16Sfloat,
            UsageBits=RD.TextureUsageBits.ColorAttachmentBit|RD.TextureUsageBits.SamplingBit|RD.TextureUsageBits.CanCopyFromBit };
        using var view=new RDTextureView();
        _softenA=_rd.TextureCreate(format,view); _softenB=_rd.TextureCreate(format,view);
        Require(_softenA,"soften A"); Require(_softenB,"soften B");
        _softenFramebufferA=_rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _softenA });
        _softenFramebufferB=_rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _softenB });
        Require(_softenFramebufferA,"soften framebuffer A"); Require(_softenFramebufferB,"soften framebuffer B");
        _softenShader=SpeedProgramRT("soften_vertex.glsl.txt","soften_fragment.glsl.txt");
        using var raster=new RDPipelineRasterizationState { CullMode=RD.PolygonCullMode.Disabled };
        using var samples=new RDPipelineMultisampleState();
        using var depth=new RDPipelineDepthStencilState();
        using var attachment=new RDPipelineColorBlendStateAttachment { WriteR=true,WriteG=true,WriteB=true,WriteA=true };
        using var blend=new RDPipelineColorBlendState { Attachments=new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
        _softenPipeline=_rd.RenderPipelineCreate(_softenShader,_rd.FramebufferGetFormat(_softenFramebufferA),RD.InvalidId,
            RD.RenderPrimitive.Triangles,raster,samples,depth,blend);
        Require(_softenPipeline,"soften pipeline");
        _softenSetA=SoftenSetRT(_speedLight); _softenSetB=SoftenSetRT(_softenA);
    }
    private Rid SoftenSetRT(Rid source)
    {
        using var light=SamplerUniform(new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=0 },source);
        using var surface=SamplerUniform(new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=1 },_surfaceColor);
        var set=_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { light,surface },_softenShader,0);
        Require(set,"soften surface/light set"); return set;
    }
    internal void RenderSoftenRT(SpeedLightFrame frame)
    {
        if(!SpeedLightReady || frame.Origin!=SurfaceOrigin) throw new InvalidOperationException("Soften has no matching light frame.");
        int steps=Math.Clamp(frame.Soften,0,10);
        _speedOutput=_speedLight;
        if(steps>0)
        {
            EnsureSoftenRT();
            Span<SoftenPush> push=stackalloc SoftenPush[1];
            for(int axis=0;axis<2;axis++)
            {
                push[0]=new SoftenPush { ScreenDirection=new Vector4(Size.X,Size.Y,axis==0 ? 1 : 0,axis==1 ? 1 : 0),Steps=steps };
                long list=_rd.DrawListBegin(axis==0 ? _softenFramebufferA : _softenFramebufferB);
                if(list==RD.InvalidId) throw new InvalidOperationException("Cannot open soften draw list.");
                try {
                    _rd.DrawListBindRenderPipeline(list,_softenPipeline);
                    _rd.DrawListBindUniformSet(list,axis==0 ? _softenSetA : _softenSetB,0);
                    _rd.DrawListSetPushConstant(list,MemoryMarshal.AsBytes(push),32);
                    _rd.DrawListDraw(list,false,1,3);
                }
                finally { _rd.DrawListEnd(); }
            }
            _speedOutput=_softenB;
        }
        SpeedLightTexture.TextureRdRid=_speedOutput;
        SpeedModeBindings.PublishLight(this,SpeedLightTexture,new Vector4(0,0,Size.X,Size.Y),frame.Ambient);
    }
    private void DisposeSoftenRT()
    {
        _speedOutput=default;
        if(_rd==null) return;
        FreeSpeedSet(ref _softenSetA); FreeSpeedSet(ref _softenSetB); Free(ref _softenPipeline); Free(ref _softenShader);
        Free(ref _softenFramebufferA); Free(ref _softenFramebufferB); Free(ref _softenA); Free(ref _softenB);
    }
}
