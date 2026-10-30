// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.Lights;
using RD=Godot.RenderingDevice;

namespace GUO.Renderer.DepthWorld;

// ADR-0048. All RD operations run on the admitted shared render thread.
internal sealed partial class OrderedWorldDevice
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LightPush
    {
        internal Vector4 Rect,WorldRadius,ColourEnergy,ReachBounds,ScreenMode,OriginCurve,AmbientRoof,Unused;
    }
    private sealed class ReachGpu
    {
        internal Rid Texture,Set;
        internal int Revision;
    }
    private readonly Dictionary<RealtimeLightField.SpeedReach,ReachGpu> _speedReachGpu=new();
    private Rid _speedLight,_speedLightFramebuffer,_speedLightShader,_speedLightPipeline;
    private Rid _speedLightSet,_speedCurvesBuffer,_speedReachSampler,_speedAmbientSet;
    private Rid _speedShadowSet,_speedBoundPolar,_speedBoundSun;
    private readonly Dictionary<Rid,Rid> _speedMobileSets=new();
    private Action _speedReleaseInputs;
    private readonly float[] _speedCurves=new float[RealtimeLightEffects.FrameLightCap*36];
    internal Texture2Drd SpeedLightTexture { get; }=new();
    internal bool SpeedLightReady { get; private set; }
    internal static long SpeedLightDrawCalls,SpeedReachUploads;

    private void EnsureLightRT()
    {
        if(_speedLight.IsValid) return;
        if(!SurfaceReady) throw new InvalidOperationException("Light pass requires this frame's surface.");
        if(!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.R8G8Unorm,RD.TextureUsageBits.SamplingBit|RD.TextureUsageBits.CanUpdateBit))
            throw new NotSupportedException("Lighting speed mode requires updateable R8G8 reach textures.");
        using var format=new RDTextureFormat { Width=(uint)Size.X,Height=(uint)Size.Y,Depth=1,
            ArrayLayers=1,Mipmaps=1,TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R16G16B16A16Sfloat,
            UsageBits=RD.TextureUsageBits.ColorAttachmentBit|RD.TextureUsageBits.SamplingBit|RD.TextureUsageBits.CanCopyFromBit };
        using var view=new RDTextureView();
        _speedLight=_rd.TextureCreate(format,view);
        Require(_speedLight,"speed light target");
        _speedLightFramebuffer=_rd.FramebufferCreate(new Godot.Collections.Array<Rid> { _speedLight });
        Require(_speedLightFramebuffer,"speed light framebuffer");
        _speedLightShader=SpeedProgramRT("light_vertex.glsl.txt","light_fragment.glsl.txt");
        using var raster=new RDPipelineRasterizationState { CullMode=RD.PolygonCullMode.Disabled };
        using var samples=new RDPipelineMultisampleState();
        using var depth=new RDPipelineDepthStencilState();
        using var attachment=new RDPipelineColorBlendStateAttachment { EnableBlend=true,
            SrcColorBlendFactor=RD.BlendFactor.One,DstColorBlendFactor=RD.BlendFactor.One,
            SrcAlphaBlendFactor=RD.BlendFactor.One,DstAlphaBlendFactor=RD.BlendFactor.One,
            WriteR=true,WriteG=true,WriteB=true,WriteA=true };
        using var blend=new RDPipelineColorBlendState { Attachments=new Godot.Collections.Array<RDPipelineColorBlendStateAttachment> { attachment } };
        _speedLightPipeline=_rd.RenderPipelineCreate(_speedLightShader,_rd.FramebufferGetFormat(_speedLightFramebuffer),
            RD.InvalidId,RD.RenderPrimitive.Triangles,raster,samples,depth,blend);
        Require(_speedLightPipeline,"speed light pipeline");
        _speedCurvesBuffer=_rd.StorageBufferCreate((uint)(_speedCurves.Length*4));
        Require(_speedCurvesBuffer,"speed curves buffer");
        using var surface=SamplerUniform(new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=0 },_surfaceColor);
        using var curves=new RDUniform { UniformType=RD.UniformType.StorageBuffer,Binding=1 };
        curves.AddId(_speedCurvesBuffer);
        _speedLightSet=_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { surface,curves },_speedLightShader,0);
        Require(_speedLightSet,"speed surface/curves set");
        using var sampler=new RDSamplerState { MinFilter=RD.SamplerFilter.Linear,MagFilter=RD.SamplerFilter.Linear,
            RepeatU=RD.SamplerRepeatMode.ClampToEdge,RepeatV=RD.SamplerRepeatMode.ClampToEdge };
        _speedReachSampler=_rd.SamplerCreate(sampler);
        Require(_speedReachSampler,"speed reach sampler");
        using var placeholder=new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=0 };
        placeholder.AddId(_speedReachSampler); placeholder.AddId(_surfaceColor);
        _speedAmbientSet=_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { placeholder },_speedLightShader,1);
        Require(_speedAmbientSet,"speed ambient set");
        SpeedLightTexture.TextureRdRid=_speedLight;
    }

    private Rid SpeedProgramRT(string vertex,string fragment)
    {
        using var source=new RDShaderSource { Language=RD.ShaderLanguage.Glsl,
            SourceVertex=SpeedShader(vertex),SourceFragment=SpeedShader(fragment) };
        using var code=_rd.ShaderCompileSpirVFromSource(source);
        if(!string.IsNullOrEmpty(code.CompileErrorVertex)||!string.IsNullOrEmpty(code.CompileErrorFragment))
            throw new InvalidOperationException(code.CompileErrorVertex+code.CompileErrorFragment);
        var shader=_rd.ShaderCreateFromSpirV(code,"guo_speed_light");
        Require(shader,"speed light shader"); return shader;
    }

    private Rid ReachSetRT(RealtimeLightField.SpeedReach reach)
    {
        if(!_speedReachGpu.TryGetValue(reach,out var gpu))
        {
            gpu=new ReachGpu();
            using var format=new RDTextureFormat { Width=(uint)reach.Side,Height=(uint)reach.Side,Depth=1,
                ArrayLayers=1,Mipmaps=1,TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R8G8Unorm,
                UsageBits=RD.TextureUsageBits.SamplingBit|RD.TextureUsageBits.CanUpdateBit };
            using var view=new RDTextureView();
            gpu.Texture=_rd.TextureCreate(format,view,new Godot.Collections.Array<byte[]> { reach.Pixels });
            Require(gpu.Texture,"speed reach texture");
            using var uniform=new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=0 };
            uniform.AddId(_speedReachSampler); uniform.AddId(gpu.Texture);
            gpu.Set=_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uniform },_speedLightShader,1);
            Require(gpu.Set,"speed reach set");
            gpu.Revision=reach.Revision; _speedReachGpu.Add(reach,gpu); SpeedReachUploads++;
        }
        else if(gpu.Revision!=reach.Revision)
        {
            if(_rd.TextureUpdate(gpu.Texture,0,reach.Pixels)!=Error.Ok)
                throw new InvalidOperationException("Cannot update speed reach texture.");
            gpu.Revision=reach.Revision; SpeedReachUploads++;
        }
        return gpu.Set;
    }

    private Rid BindShadowInputsRT(SpeedLightFrame frame,Rid mobile=default)
    {
        Rid polar=frame.LampShadowAtlas.IsValid ? frame.LampShadowAtlas : _surfaceColor;
        Rid sun=frame.SunMask.IsValid ? frame.SunMask : _surfaceColor;
        if(_speedBoundPolar!=polar || _speedBoundSun!=sun) {
            foreach(var set in _speedMobileSets.Values) if(_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
            _speedMobileSets.Clear();
        }
        _speedBoundPolar=polar; _speedBoundSun=sun;
        if(_speedMobileSets.TryGetValue(mobile,out var cached) && _rd.UniformSetIsValid(cached)) return cached;
        using var a=SamplerUniform(new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=0 },polar);
        using var b=SamplerUniform(new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=1 },sun);
        using var c=SamplerUniform(new RDUniform { UniformType=RD.UniformType.SamplerWithTexture,Binding=2 },mobile.IsValid ? mobile : _surfaceColor);
        _speedShadowSet=_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { a,b,c },_speedLightShader,2);
        Require(_speedShadowSet,"speed shadow inputs");
        _speedMobileSets[mobile]=_speedShadowSet;
        return _speedShadowSet;
    }

    internal void RenderLightsRT(SpeedLightFrame frame)
    {
        SpeedLightReady=false;
        if(!SurfaceReady || frame.Origin!=SurfaceOrigin) throw new InvalidOperationException("Mismatched light/surface ticket.");
        try
        {
            EnsureLightRT();
            if(_speedReleaseInputs!=frame.ReleaseInputs) { _speedReleaseInputs?.Invoke(); _speedReleaseInputs=frame.ReleaseInputs; }
            frame.TargetSize=Size;
            frame.RenderInputs?.Invoke(_rd,frame);
            Rid ambientShadows=BindShadowInputsRT(frame);
            if(_speedReachGpu.Count>512) ClearReachGpuRT();
            // Resolve/update all resources before opening the draw list.
            Span<Rid> sets=stackalloc Rid[frame.Count];
            Span<Rid> shadows=stackalloc Rid[frame.Count];
            bool customCurves=false;
            for(int i=0;i<frame.Count;i++)
            {
                ref var light=ref frame.Lights[i];
                sets[i]=ReachSetRT(light.Reach);
                shadows[i]=BindShadowInputsRT(frame,light.MobileShadow);
                if(light.Source.CustomCurve is { Length:>1 } custom) {
                    customCurves=true;
                    for(int j=0;j<33;j++) _speedCurves[i*36+j]=RealtimeLightProfiles.Evaluate(LightCurve.Custom,j/32f,custom);
                }
            }
            if(customCurves) Upload(_speedCurvesBuffer,MemoryMarshal.AsBytes(_speedCurves.AsSpan(0,frame.Count*36)),0);
            // With no lights, a valid bound reach descriptor is still required by
            // the shared shader. Use the surface as an unused sampler in ambient mode.
            {
                // Godot's Colors.Transparent is transparent WHITE; additive HDR
                // must start with zero RGB as well as zero alpha.
                _clear[0]=new Color(0,0,0,0);
                long list=_rd.DrawListBegin(_speedLightFramebuffer,RD.DrawFlags.ClearColor0,_clear);
                if(list==RD.InvalidId) throw new InvalidOperationException("Cannot open speed light draw list.");
                try
                {
                    _rd.DrawListBindRenderPipeline(list,_speedLightPipeline);
                    _rd.DrawListBindUniformSet(list,_speedLightSet,0);
                    _rd.DrawListBindUniformSet(list,ambientShadows,2);
                    _rd.DrawListBindUniformSet(list,frame.Count>0 ? sets[0] : _speedAmbientSet,1);
                    Span<LightPush> push=stackalloc LightPush[1];
                    push[0]=new LightPush { Rect=new Vector4(0,0,Size.X,Size.Y),
                        ScreenMode=new Vector4(Size.X,Size.Y,-1,0),
                        AmbientRoof=new Vector4(frame.Ambient.X,frame.Ambient.Y,frame.Ambient.Z,frame.RoofShare),
                        Unused=new Vector4(-1,frame.ShadowSoftnessTenths,0,frame.SunMask.IsValid ? 1 : 0) };
                    _rd.DrawListSetPushConstant(list,MemoryMarshal.AsBytes(push),128);
                    _rd.DrawListDraw(list,false,1,6); SpeedLightDrawCalls++;
                    for(int i=0;i<frame.Count;i++)
                    {
                        ref var light=ref frame.Lights[i]; var source=light.Source;
                        var reach=light.Reach; float r=light.PixelRadius;
                        push[0]=new LightPush {
                            Rect=new Vector4(light.PixelCentre.X-r,light.PixelCentre.Y-r,r*2,r*2),
                            WorldRadius=new Vector4(source.X+source.OffsetX+0.5f-frame.Origin.X,
                                source.Y+source.OffsetY+0.5f-frame.Origin.Y,source.Z+10,source.Radius),
                            ColourEnergy=new Vector4(source.Color.R,source.Color.G,source.Color.B,source.Energy*light.Flicker),
                            ReachBounds=new Vector4(reach.X,reach.Y,reach.Side/(float)reach.Scale,reach.Side/(float)reach.Scale),
                            ScreenMode=new Vector4(Size.X,Size.Y,i,light.MobileShadow.IsValid ? 1 : 0),
                            OriginCurve=new Vector4(frame.Origin.X,frame.Origin.Y,(int)source.Curve,0),
                            Unused=new Vector4(light.ShadowRow,frame.ShadowSoftnessTenths,frame.LampShadowAtlas.IsValid ? 1 : 0,0) };
                        _rd.DrawListBindUniformSet(list,sets[i],1);
                        _rd.DrawListBindUniformSet(list,shadows[i],2);
                        _rd.DrawListSetPushConstant(list,MemoryMarshal.AsBytes(push),128);
                        _rd.DrawListDraw(list,false,1,6); SpeedLightDrawCalls++;
                    }
                }
                finally { _rd.DrawListEnd(); }
            }
            SpeedLightReady=true;
            frame.RenderPatches?.Invoke(_rd,_speedLightFramebuffer,_surfaceColor,frame.Origin,Size);
            _speedOutput=_speedLight;
            SpeedLightTexture.TextureRdRid=_speedLight;
            SpeedModeBindings.PublishLight(this,SpeedLightTexture,new Vector4(0,0,Size.X,Size.Y),frame.Ambient);
        }
        catch { DisposeLightRT(); throw; }
    }

    private void ClearReachGpuRT()
    {
        foreach(var gpu in _speedReachGpu.Values) { FreeSpeedSet(ref gpu.Set); Free(ref gpu.Texture); }
        _speedReachGpu.Clear();
    }
    internal byte[] ReadSpeedLightRT()=>_rd.TextureGetData(_speedOutput,0);
    internal void DisableLightRT()
    {
        DisposeLightRT();
        if(SurfaceReady) SpeedModeBindings.PublishSurface(this,SurfaceTexture,SurfaceOrigin,Size);
        else SpeedModeBindings.Clear(this);
    }
    private void DisposeLightRT()
    {
        DisposeSoftenRT();
        SpeedLightReady=false; SpeedLightTexture.TextureRdRid=default;
        if(_rd==null) return;
        ClearReachGpuRT();
        foreach(var set in _speedMobileSets.Values) if(_rd.UniformSetIsValid(set)) _rd.FreeRid(set);
        _speedMobileSets.Clear(); _speedShadowSet=_speedBoundPolar=_speedBoundSun=default;
        _speedReleaseInputs?.Invoke(); _speedReleaseInputs=null;
        FreeSpeedSet(ref _speedLightSet); FreeSpeedSet(ref _speedAmbientSet); Free(ref _speedCurvesBuffer); Free(ref _speedReachSampler);
        Free(ref _speedLightPipeline); Free(ref _speedLightShader);
        Free(ref _speedLightFramebuffer); Free(ref _speedLight);
    }
}
