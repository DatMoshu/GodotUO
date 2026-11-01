// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048 amendment: one wall-shadow compute dispatch per frame.
using System;
using System.Runtime.InteropServices;
using Godot;
using RD = Godot.RenderingDevice;
using N4 = System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.Shadows;

internal sealed class PolarShadowMapRd : IDisposable
{
    private RD _rd;
    private Rid _shader,_pipeline,_atlas,_grid,_lamps,_set;
    private int _width,_height;
    private WallShadowGrid _lastGrid;
    private ulong _gridVersion;
    private byte[] _gridSnapshot=Array.Empty<byte>();
    private bool _uploaded;
    private bool _disposed;
    private readonly N4[] _lampData = new N4[64];
    public Texture2Drd Texture { get; } = new();
    public Rid Atlas => _atlas;
    public string Failure { get; private set; }
    public int LampCount { get; private set; }
    internal int GridUploads { get; private set; }

    // Render thread only. grid must remain frozen until this transaction ends.
    public bool Render(RD device,WallShadowGrid grid,ReadOnlySpan<ShadowLamp> lamps,bool publish=true)
    {
        LampCount=0;
        if (_disposed || Failure!=null || lamps.Length==0) return false;
        try
        {
            if (lamps.Length>10) throw new ArgumentOutOfRangeException(nameof(lamps));
            _rd ??= device ?? throw new NotSupportedException("Wall shadows require the Depth World RenderingDevice.");
            if (_rd!=device) throw new InvalidOperationException("Wall shadow device changed.");
            Ensure(grid);
            if (!_uploaded || !ReferenceEquals(_lastGrid,grid) || _gridVersion!=grid.Version)
            {
                // Stable cached grids cost O(1). Rebuilt snapshots use the
                // runtime's vectorized byte comparison, not a per-byte hash.
                if (!_uploaded || !grid.Bytes.SequenceEqual(_gridSnapshot))
                {
                    int layerBytes=checked(grid.Width*grid.Height*16);
                    if (_rd.TextureUpdate(_grid,0,grid.Bytes[..layerBytes])!=Error.Ok ||
                        _rd.TextureUpdate(_grid,1,grid.Bytes[layerBytes..])!=Error.Ok)
                        throw new InvalidOperationException("Wall grid upload failed.");
                    if (_gridSnapshot.Length!=grid.Bytes.Length) _gridSnapshot=new byte[grid.Bytes.Length];
                    grid.Bytes.CopyTo(_gridSnapshot);
                    _uploaded=true; GridUploads++;
                }
                _lastGrid=grid; _gridVersion=grid.Version;
            }
            for (int i=0; i<lamps.Length; i++)
            {
                var p=lamps[i].Position;
                _lampData[i]=new N4(p.X-grid.OriginX,p.Y-grid.OriginY,p.Z,lamps[i].Radius);
            }
            var bytes=MemoryMarshal.AsBytes(_lampData.AsSpan());
            if (_rd.BufferUpdate(_lamps,0,(uint)bytes.Length,bytes)!=Error.Ok)
                throw new InvalidOperationException("Wall lamp upload failed.");
            _rd.CaptureTimestamp("speed.wall-shadow.begin");
            long list=_rd.ComputeListBegin();
            if (list==RD.InvalidId) throw new InvalidOperationException("Wall compute list failed.");
            try
            {
                _rd.ComputeListBindComputePipeline(list,_pipeline);
                _rd.ComputeListBindUniformSet(list,_set,0);
                // One workgroup per light, 256 rays per workgroup.
                _rd.ComputeListDispatch(list,1,(uint)lamps.Length,1);
            }
            finally { _rd.ComputeListEnd(); }
            _rd.CaptureTimestamp("speed.wall-shadow.end");
            if (publish) Texture.TextureRdRid=_atlas;
            LampCount=lamps.Length;
            return true;
        }
        catch (Exception error)
        {
            Failure=error.Message; Release();
            GD.PrintErr("[speed-shadows] Wall shadows disabled: "+Failure);
            return false;
        }
    }

    private void Ensure(WallShadowGrid grid)
    {
        if (!_shader.IsValid)
        {
            var usage=RD.TextureUsageBits.StorageBit | RD.TextureUsageBits.SamplingBit | RD.TextureUsageBits.CanCopyFromBit;
            if (!_rd.TextureIsFormatSupportedForUsage(RD.DataFormat.R16G16Sfloat,usage))
                throw new NotSupportedException("RG16F storage wall-shadow atlas unsupported.");
            using var source=new RDShaderSource { Language=RD.ShaderLanguage.Glsl, SourceCompute=ComputeGlsl };
            using RDShaderSpirV spirv=_rd.ShaderCompileSpirVFromSource(source,true);
            if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException(spirv.CompileErrorCompute);
            _shader=_rd.ShaderCreateFromSpirV(spirv,"guo_wall_polar_compute"); Require(_shader);
            _pipeline=_rd.ComputePipelineCreate(_shader); Require(_pipeline);
            _atlas=CreateTexture(256,64,1,RD.DataFormat.R16G16Sfloat,usage); Require(_atlas);
            _lamps=_rd.StorageBufferCreate(64*16); Require(_lamps);
        }
        if (_width==grid.Width && _height==grid.Height && _set.IsValid) return;
        Free(ref _set); Free(ref _grid);
        _grid=CreateTexture((uint)grid.Width,(uint)grid.Height,2,RD.DataFormat.R32G32B32A32Sfloat,
            RD.TextureUsageBits.StorageBit | RD.TextureUsageBits.CanUpdateBit);
        Require(_grid); _width=grid.Width; _height=grid.Height; _uploaded=false;
        using var lamps=new RDUniform { UniformType=RD.UniformType.StorageBuffer, Binding=0 };
        using var cells=new RDUniform { UniformType=RD.UniformType.Image, Binding=1 };
        using var atlas=new RDUniform { UniformType=RD.UniformType.Image, Binding=2 };
        lamps.AddId(_lamps); cells.AddId(_grid); atlas.AddId(_atlas);
        _set=_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { lamps,cells,atlas },_shader,0);
        Require(_set);
    }

    private Rid CreateTexture(uint width,uint height,uint layers,RD.DataFormat format,RD.TextureUsageBits usage)
    {
        using var spec=new RDTextureFormat { Width=width,Height=height,Depth=1,ArrayLayers=layers,Mipmaps=1,
            TextureType=layers==1 ? RD.TextureType.Type2D : RD.TextureType.Type2DArray,
            Format=format,Samples=RD.TextureSamples.Samples1,UsageBits=usage };
        using var view=new RDTextureView();
        return _rd.TextureCreate(spec,view);
    }
    private static void Require(Rid rid) { if (!rid.IsValid) throw new InvalidOperationException("Wall shadow resource creation failed."); }
    private void Free(ref Rid rid) { if (rid.IsValid) _rd.FreeRid(rid); rid=default; }
    private void Release()
    {
        LampCount=0; Texture.TextureRdRid=default;
        if (_rd==null) return;
        Free(ref _set); Free(ref _grid); Free(ref _lamps); Free(ref _atlas); Free(ref _pipeline); Free(ref _shader);
        _width=_height=0; _uploaded=false; _lastGrid=null;
        _gridSnapshot=Array.Empty<byte>();
    }
    public void Dispose() { if (_disposed) return; _disposed=true; Release(); }

    internal const string ComputeGlsl="""
        #version 450
        layout(local_size_x=256,local_size_y=1,local_size_z=1) in;
        layout(set=0,binding=0,std430) readonly buffer Lamps { vec4 lamps[]; };
        layout(set=0,binding=1,rgba32f) uniform readonly image2DArray walls;
        layout(set=0,binding=2,rg16f) uniform writeonly image2D polar;
        void main() {
            int angle=int(gl_LocalInvocationID.x),row=int(gl_WorkGroupID.y);
            vec4 lamp=lamps[row];
            float theta=(float(angle)+0.5)/256.0*6.28318530718-3.14159265359;
            vec2 ray=vec2(cos(theta),sin(theta));
            ivec2 cell=ivec2(floor(lamp.xy)),start=cell,step=ivec2(sign(ray));
            vec2 delta=1.0/max(abs(ray),vec2(0.000001));
            vec2 edge=(vec2(cell)+vec2(greaterThan(ray,vec2(0)))-lamp.xy)/ray;
            float enter=0.0,hit=65504.0,top=-32768.0;
            ivec2 size=imageSize(walls).xy;
            for (int n=0;n<128;n++) {
                if (enter>lamp.w) break;
                float leave=min(min(edge.x,edge.y),lamp.w);
                if (all(greaterThanEqual(cell,ivec2(0))) && all(lessThan(cell,size))) {
                    vec4 w=imageLoad(walls,ivec3(cell,0));
                    float offX=imageLoad(walls,ivec3(cell,1)).x;
                    float nearest=65504.0,z=-32768.0;
                    // The emitter's own solid cell cannot shadow its light.
                    if (w.x>-32768.0 && any(notEqual(cell,start))) { nearest=max(enter,0.0); z=w.x; }
                    if (w.y>-32768.0 && abs(ray.y)>0.000001) {
                        float t=(float(cell.y)+w.z-lamp.y)/ray.y;
                        if (t>=max(enter,0.001) && t<=leave+0.00001 && t<nearest) { nearest=t; z=w.y; }
                    }
                    if (w.w>-32768.0 && abs(ray.x)>0.000001) {
                        float t=(float(cell.x)+offX-lamp.x)/ray.x;
                        if (t>=max(enter,0.001) && t<=leave+0.00001 && t<nearest) { nearest=t; z=w.w; }
                    }
                    if (nearest<=lamp.w) { hit=nearest; top=z; break; }
                }
                if (edge.x<edge.y) { enter=edge.x; edge.x+=delta.x; cell.x+=step.x; }
                else { enter=edge.y; edge.y+=delta.y; cell.y+=step.y; }
            }
            imageStore(polar,ivec2(angle,row),vec4(hit,top,0,0));
        }
        """;
}
