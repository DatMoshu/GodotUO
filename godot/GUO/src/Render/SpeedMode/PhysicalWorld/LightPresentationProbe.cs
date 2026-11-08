// SPDX-License-Identifier: BSD-2-Clause
// Real shipped transfer shader, real HDR/R8 readbacks; no native artwork required.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using GUO.Renderer.DepthWorld;
using RD = Godot.RenderingDevice;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class LightPresentationProbe : Node
{
    private readonly List<object> _rows=new();
    private int _checks;
    public override void _Ready()
    {
        try { Run(); Save(null); GD.Print($"[light-presentation] PASS checks={_checks}; backend=gpu-raster-half-r8"); GetTree().Quit(0); }
        catch(Exception error) { Save(error.ToString()); GD.PrintErr("[light-presentation] FAIL "+error); GetTree().Quit(1); }
    }
    private void Check(bool ok,string reason) { if(!ok)throw new InvalidOperationException(reason);_checks++; }
    private void Run()
    {
        RD rd=RenderingServer.CreateLocalRenderingDevice();
        Check(rd!=null,"local GPU raster device available");
        var transfer=new LightPresentationPassRd();
        var repeated=new LightPresentationPassRd();
        Rid source=default, art=default, resized=default, rebound=default;
        try
        {
            float[] input={4,2,1,.25f, 4,2,1,.5f, .5f,.25f,.125f,.75f,
                .75f,.75f,.75f,1, 0,0,0,.5f, 1,.5f,.25f,.125f, 1,2,4,.33325195f};
            var size=new Vector2I(7,1);
            byte[] original=HalfBytes(input);
            source=Texture(rd,size,original);
            Rid output=transfer.Render(rd,size,source);rd.Submit();rd.Sync();
            byte[] first=rd.TextureGetData(output,0);
            Check(first.Length==original.Length,"actual RGBA16F transfer target size");
            Check(rd.TextureGetData(source,0).AsSpan().SequenceEqual(original),"raw HDR accumulation remains bit-exact and unclamped");
            for(int pixel=0;pixel<7;pixel++)
            {
                float max=Math.Max(input[pixel*4],Math.Max(input[pixel*4+1],input[pixel*4+2]));
                for(int channel=0;channel<4;channel++)
                {
                    float expected=channel<3 && max>1 ? input[pixel*4+channel]/max:input[pixel*4+channel];
                    float actual=ReadHalf(first,pixel*4+channel);
                    _rows.Add(new {pixel,channel,actual,expected});
                    Check(Math.Abs(actual-expected)<.0005f,"shipped transfer equation pixel "+pixel+" channel "+channel);
                }
                Check(first.AsSpan(pixel*8+6,2).SequenceEqual(original.AsSpan(pixel*8+6,2)),"alpha is bit-exact pixel "+pixel);
                if(max<=1)Check(first.AsSpan(pixel*8,8).SequenceEqual(original.AsSpan(pixel*8,8)),"dim/ambient/black/unit range remains bit-exact pixel "+pixel);
            }
            Check(ReadHalf(first,0)/ReadHalf(first,1)==2 && ReadHalf(first,1)/ReadHalf(first,2)==2,
                "warm HDR channel ratios survive transfer without independent channel clipping");
            Rid second=repeated.Render(rd,size,output);rd.Submit();rd.Sync();
            Check(rd.TextureGetData(second,0).AsSpan().SequenceEqual(first),"presentation normalization is GPU bit-exact idempotent");

            float[] artPixels={.9f,.85f,.8f,1, .55f,.5f,.45f,1, .8f,.8f,.8f,1,
                .8f,.8f,.8f,1, .8f,.8f,.8f,1, .8f,.8f,.8f,1, .8f,.8f,.8f,1};
            art=Texture(rd,size,HalfBytes(artPixels));
            byte[] oldComposite=Composite(rd,size,source,art);
            byte[] newComposite=Composite(rd,size,output,art);
            _rows.Add(new {comparison="actual-final-R8-clamp",oldBright=oldComposite[..4],oldDark=oldComposite[4..8],
                newBright=newComposite[..4],newDark=newComposite[4..8]});
            Check(oldComposite[0]==255 && oldComposite[4]==255 && oldComposite[1]==255 && oldComposite[5]==255,
                "raw HDR reproduction clips both pale-art detail samples in red and green");
            Check(newComposite[0]-newComposite[4]>80 && newComposite[1]-newComposite[5]>35,
                "real GPU transfer preserves pale-art red/green texture contrast after final R8 clamp");
            for(int pixel=2;pixel<6;pixel++)Check(oldComposite.AsSpan(pixel*4,4).SequenceEqual(newComposite.AsSpan(pixel*4,4)),
                "dim final art composite unchanged pixel "+pixel);

            // The second pass borrowed the first target; retire it before resizing that source.
            repeated.Dispose();
            var small=new Vector2I(2,2);
            float[] smallInput={8,4,2,1, .25f,.5f,.75f,.5f, 0,0,0,1, 1,1,1,1};
            resized=Texture(rd,small,HalfBytes(smallInput));
            Rid changed=transfer.Render(rd,small,resized);rd.Submit();rd.Sync();
            byte[] changedBytes=rd.TextureGetData(changed,0);
            Check(changedBytes.Length==32 && ReadHalf(changedBytes,0)==1 && ReadHalf(changedBytes,1)==.5f
                && ReadHalf(changedBytes,2)==.25f && ReadHalf(changedBytes,4)==.25f,"resized first ticket uses new HDR source");
            float[] other={2,4,8,.25f, .75f,.5f,.25f,1, 0,0,0,1, 1,1,1,1};
            rebound=Texture(rd,small,HalfBytes(other));
            changed=transfer.Render(rd,small,rebound);rd.Submit();rd.Sync();
            changedBytes=rd.TextureGetData(changed,0);
            Check(ReadHalf(changedBytes,0)==.25f && ReadHalf(changedBytes,1)==.5f && ReadHalf(changedBytes,2)==1
                && ReadHalf(changedBytes,3)==.25f,"same-size source rebind uses new pixels and alpha immediately");
            transfer.Dispose();
            Check(!transfer.Output.IsValid,"dispose revokes presentation output");
            transfer.Dispose();
        }
        finally
        {
            repeated.Dispose();transfer.Dispose();
            if(source.IsValid)rd.FreeRid(source);if(art.IsValid)rd.FreeRid(art);
            if(resized.IsValid)rd.FreeRid(resized);if(rebound.IsValid)rd.FreeRid(rebound);rd.Free();
        }
    }

    private Rid Texture(RD rd,Vector2I size,byte[] bytes)
    {
        using var spec=new RDTextureFormat { Width=(uint)size.X,Height=(uint)size.Y,Depth=1,ArrayLayers=1,Mipmaps=1,
            TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R16G16B16A16Sfloat,Samples=RD.TextureSamples.Samples1,
            UsageBits=RD.TextureUsageBits.SamplingBit|RD.TextureUsageBits.CanUpdateBit|RD.TextureUsageBits.CanCopyFromBit };
        using var view=new RDTextureView();
        Rid texture=rd.TextureCreate(spec,view,new Godot.Collections.Array<byte[]> {bytes});
        Check(texture.IsValid,"HDR fixture texture created");return texture;
    }
    private static byte[] HalfBytes(float[] values)
    {
        var bytes=new byte[values.Length*2];
        for(int i=0;i<values.Length;i++)BitConverter.TryWriteBytes(bytes.AsSpan(i*2,2),BitConverter.HalfToUInt16Bits((Half)values[i]));
        return bytes;
    }
    private static float ReadHalf(byte[] bytes,int component) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes,component*2));

    // Independent native-style multiplication and final UNorm clamp. The lighting
    // source is the actual shipped presentation pass output, not a CPU substitute.
    private byte[] Composite(RD rd,Vector2I size,Rid light,Rid art)
    {
        var owned=new List<Rid>();
        try
        {
            using var spec=new RDTextureFormat { Width=(uint)size.X,Height=(uint)size.Y,Depth=1,ArrayLayers=1,Mipmaps=1,
                TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R8G8B8A8Unorm,Samples=RD.TextureSamples.Samples1,
                UsageBits=RD.TextureUsageBits.ColorAttachmentBit|RD.TextureUsageBits.CanCopyFromBit };
            using var view=new RDTextureView();
            Rid target=rd.TextureCreate(spec,view);owned.Add(target);
            Rid framebuffer=rd.FramebufferCreate(new Godot.Collections.Array<Rid>{target});owned.Add(framebuffer);
            using var source=new RDShaderSource { Language=RD.ShaderLanguage.Glsl,SourceVertex=VertexGlsl,SourceFragment=FragmentGlsl };
            using var code=rd.ShaderCompileSpirVFromSource(source);
            Check(string.IsNullOrEmpty(code.CompileErrorVertex)&&string.IsNullOrEmpty(code.CompileErrorFragment),"independent final-clamp shader compiles");
            Rid shader=rd.ShaderCreateFromSpirV(code,"guo_presentation_probe_composite");owned.Add(shader);
            using var raster=new RDPipelineRasterizationState {CullMode=RD.PolygonCullMode.Disabled};
            using var samples=new RDPipelineMultisampleState();using var depth=new RDPipelineDepthStencilState();
            using var attachment=new RDPipelineColorBlendStateAttachment {WriteR=true,WriteG=true,WriteB=true,WriteA=true};
            using var blend=new RDPipelineColorBlendState {Attachments=new Godot.Collections.Array<RDPipelineColorBlendStateAttachment>{attachment}};
            Rid pipeline=rd.RenderPipelineCreate(shader,rd.FramebufferGetFormat(framebuffer),RD.InvalidId,RD.RenderPrimitive.Triangles,
                raster,samples,depth,blend);owned.Add(pipeline);
            using var samplerState=new RDSamplerState {MinFilter=RD.SamplerFilter.Nearest,MagFilter=RD.SamplerFilter.Nearest};
            Rid sampler=rd.SamplerCreate(samplerState);owned.Add(sampler);
            using var l=new RDUniform {UniformType=RD.UniformType.SamplerWithTexture,Binding=0};l.AddId(sampler);l.AddId(light);
            using var a=new RDUniform {UniformType=RD.UniformType.SamplerWithTexture,Binding=1};a.AddId(sampler);a.AddId(art);
            Rid set=rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>{l,a},shader,0);owned.Add(set);
            foreach(Rid rid in owned)Check(rid.IsValid,"independent composite GPU resource valid");
            long list=rd.DrawListBegin(framebuffer);
            Check(list!=RD.InvalidId,"independent final-clamp draw opens");
            try {rd.DrawListBindRenderPipeline(list,pipeline);rd.DrawListBindUniformSet(list,set,0);rd.DrawListDraw(list,false,1,3);}
            finally {rd.DrawListEnd();}
            rd.Submit();rd.Sync();return rd.TextureGetData(target,0);
        }
        finally {for(int i=owned.Count-1;i>=0;i--)if(owned[i].IsValid)rd.FreeRid(owned[i]);}
    }
    private const string VertexGlsl="""
        #version 450
        void main(){const vec2 p[3]=vec2[3](vec2(-1,-1),vec2(3,-1),vec2(-1,3));gl_Position=vec4(p[gl_VertexIndex],0,1);}
        """;
    private const string FragmentGlsl="""
        #version 450
        layout(set=0,binding=0)uniform sampler2D lighting;
        layout(set=0,binding=1)uniform sampler2D artwork;
        layout(location=0)out vec4 colour;
        void main(){ivec2 p=ivec2(gl_FragCoord.xy);vec4 a=texelFetch(artwork,p,0);colour=vec4(a.rgb*texelFetch(lighting,p,0).rgb,a.a);}
        """;
    private void Save(string failure)
    {
        string root=System.Environment.GetEnvironmentVariable("GUO_PHYSICAL_WORLD_OUT");if(string.IsNullOrWhiteSpace(root))return;
        root=Path.GetFullPath(root);string project=Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        if(root.StartsWith(project,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("GPU fixture evidence must stay outside project.");
        string folder=Path.Combine(root,"data");Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"light-presentation-gpu-results.json"),JsonSerializer.Serialize(
            new {backend="gpu-raster-half-r8",checks=_checks,rows=_rows,failure},new JsonSerializerOptions {WriteIndented=true}));
    }
}
