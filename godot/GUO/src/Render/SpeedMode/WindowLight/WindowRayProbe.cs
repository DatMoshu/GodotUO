// SPDX-License-Identifier: BSD-2-Clause
// Synthetic receiver/visibility/lifetime proof. No art, shard or timing claims.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using GUO.Renderer.SpeedMode.Shadows;
using Godot;
using RD=Godot.RenderingDevice;
using N2=System.Numerics.Vector2;
using N4=System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.WindowLight;

public partial class WindowRayProbe : Node
{
    public override void _Ready()
    {
        try { Run(Array.IndexOf(OS.GetCmdlineUserArgs(),"--gpu")>=0);GetTree().Quit(0); }
        catch(Exception e) { GD.PrintErr("[window-ray-probe] FAIL "+e);GetTree().Quit(1); }
    }
    private static void Check(bool ok,string text) { if(!ok) throw new InvalidOperationException(text); }
    internal static void Run(bool gpu)
    {
        Check(Marshal.SizeOf<WindowRayPatch>()==WindowRayPatch.Bytes,"160-byte record");
        Check(WindowLightPatch.TryCreate(WindowLightTable.BuiltIn[0xF03D],new N2(5000,6000),0,N2.UnitX,N2.UnitY,N2.UnitY,
            true,true,1,new N4(0,0,128,128),N4.One,out var window),"window source");
        Check(WindowRayPatch.TryCreate(window,new Vector2(64,0),true,true,true,out var patch),"current covered/open/day source");
        foreach(var gates in new[]{(false,true,true),(true,false,true),(true,true,false)})
            Check(!WindowRayPatch.TryCreate(window,Vector2.Zero,gates.Item1,gates.Item2,gates.Item3,out _),"source gate rejects");
        var invalid=window;invalid.Origin.W=float.NaN;
        Check(!WindowRayPatch.TryCreate(invalid,Vector2.Zero,true,true,true,out _),"invalid snapshot rejects");
        var gate=new WindowRayGate();var origin=new Vector2I(5000,6000);var size=new Vector2I(128,128);
        Check(!gate.Consume(1,origin,size),"cold has no history");
        gate.Prepare(1,true,origin,size);Check(!gate.Consume(2,origin,size),"stale snapshot rejects");
        gate.Prepare(1,true,origin,size);Check(!gate.Consume(1,origin+Vector2I.One,size),"camera origin rejects");
        gate.Prepare(1,true,origin,size);Check(!gate.Consume(1,origin,size*2),"resize rejects");
        gate.Prepare(1,false,origin,size);Check(!gate.Consume(1,origin,size),"failed/missing sun mask rejects");
        gate.Prepare(1,true,origin,size);Check(gate.Consume(1,origin,size)&&!gate.Consume(1,origin,size),"one current callback only");
        gate.Prepare(1,true,origin,size);gate.Clear();Check(!gate.Consume(1,origin,size),"off/release clears");
        // Simulate borrowed views disappearing after ticket admission. The
        // real callback must reject the whole core transaction, not publish it.
        var inputs=new SpeedShadowInputs();inputs.Rays.Add(patch);
        gate.Prepare(0,true,origin,size);
        typeof(SpeedShadowInputs).GetField("_rayGate",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(inputs,gate);
        bool rejected=false;
        try { inputs.RenderWindows(null,default,default,origin,size); }
        catch(InvalidOperationException) { rejected=true; }
        finally { inputs.Release(); }
        Check(rejected,"callback failure reaches core transaction fallback");
        inputs.RenderWindows(null,default,default,origin,size); // retired ticket is a no-op
        if(gpu) Gpu(patch);
        GD.Print($"[window-ray-probe] PASS backend={(gpu?"gpu":"contract")} receiver=covered-floor-low-only stale=reject failure=reject off-diff={(gpu?"0":"not-rendered")} cap=16 steps=8 buffer<=4096 resources<=6");
    }

    private static void Gpu(WindowRayPatch patch)
    {
        var rd=RenderingServer.CreateLocalRenderingDevice()??throw new NotSupportedException("Use a leased Mobile/Forward+ runtime.");
        var pass=new WindowRayPassRd();Rid target=default,framebuffer=default;
        try {
            const int size=128;
            using var view=new RDTextureView();
            using var format=new RDTextureFormat { Format=RD.DataFormat.R16G16B16A16Sfloat,Width=size,Height=size,Depth=1,ArrayLayers=1,Mipmaps=1,
                TextureType=RD.TextureType.Type2D,Samples=RD.TextureSamples.Samples1,UsageBits=RD.TextureUsageBits.ColorAttachmentBit|RD.TextureUsageBits.CanCopyFromBit };
            target=rd.TextureCreate(format,view,new Godot.Collections.Array<byte[]>());
            framebuffer=rd.FramebufferCreate(new Godot.Collections.Array<Rid>{target},RD.InvalidId,1);
            void Clear() { var list=rd.DrawListBegin(framebuffer,RD.DrawFlags.ClearColor0,new Color[]{new(0,0,0,0)},1,0,null,0);Check(list!=RD.InvalidId,"fixture clear");rd.DrawListEnd(); }
            byte[] Read() { rd.Submit();rd.Sync();return rd.TextureGetData(target,0); }
            float Red(byte[] data,int x,int y)=>(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data,(y*size+x)*8));
            Clear();var baseline=Read();
            Check(pass.Draw(rd,framebuffer,default,default,N2.Zero,size,size,ReadOnlySpan<WindowRayPatch>.Empty)&&pass.ResourceCount==0,"off allocates nothing");
            Check(baseline.AsSpan().SequenceEqual(Read()),"off exact pixels");
            byte[] Case(string name,int copies=1) {
                float[] surface=new float[size*size*4];byte[] mask=new byte[size*size];
                for(int y=0;y<size;y++) for(int x=0;x<size;x++) {
                    int at=(y*size+x)*4;surface[at]=(x+.5f)/size*1.14f;surface[at+1]=(y+.5f)/size*2.5f;
                    surface[at+2]=x==20?5:x==25?2:0;
                    surface[at+3]=x==5?18:x==10?0:x==15?1:x is 20 or 25?20:17;
                    if(y is >=12 and <=25) {
                        if(name=="wall") surface[at+3]=18;
                        if(name=="cover") surface[at+3]=1;
                        if(name=="height") surface[at+2]=10;
                        if(name=="sun-mask") mask[y*size+x]=255;
                    }
                }
                using var sf=new RDTextureFormat { Format=RD.DataFormat.R32G32B32A32Sfloat,Width=size,Height=size,Depth=1,ArrayLayers=1,Mipmaps=1,
                    TextureType=RD.TextureType.Type2D,Samples=RD.TextureSamples.Samples1,UsageBits=RD.TextureUsageBits.SamplingBit };
                using var mf=new RDTextureFormat { Format=RD.DataFormat.R8Unorm,Width=size,Height=size,Depth=1,ArrayLayers=1,Mipmaps=1,
                    TextureType=RD.TextureType.Type2D,Samples=RD.TextureSamples.Samples1,UsageBits=RD.TextureUsageBits.SamplingBit };
                Rid s=rd.TextureCreate(sf,view,new Godot.Collections.Array<byte[]>{MemoryMarshal.AsBytes(surface.AsSpan()).ToArray()});
                Rid m=rd.TextureCreate(mf,view,new Godot.Collections.Array<byte[]>{mask});
                try {
                    Clear();var patches=new WindowRayPatch[copies];Array.Fill(patches,patch);
                    Check(pass.Draw(rd,framebuffer,s,m,new N2(5000,6000),size,size,patches),pass.Failure??"shaft draw");
                    var bytes=Read();Check(pass.ResourceCount<=6&&pass.BufferBytes<=4096,"resource bound after descriptor replacement");return bytes;
                }
                finally { rd.FreeRid(s);rd.FreeRid(m); }
            }
            var clear=Case("clear");Check(Red(clear,32,32)>.005f,"covered floor receives shaft");
            Check(Red(clear,5,32)==0&&Red(clear,10,32)==0&&Red(clear,15,32)==0&&Red(clear,20,32)==0,$"wall/sky/uncovered/high upright reject values={Red(clear,5,32)},{Red(clear,10,32)},{Red(clear,15,32)},{Red(clear,20,32)}");
            Check(Red(clear,25,32)>0,"low upright receives shaft");
            foreach(string test in new[]{"wall","cover","height","sun-mask"}) Check(Red(Case(test),32,64)==0,"path occlusion "+test);
            var capped=Case("clear",WindowRayPatch.Limit);Check(Red(capped,32,32)<=.64f,"bounded additive energy");
            string folder=System.Environment.GetEnvironmentVariable("GUO_SHADOW_PROBE_OUT");
            if(!string.IsNullOrWhiteSpace(folder)) {
                using var image=Image.CreateFromData(size,size,false,Image.Format.Rgbah,clear);image.Convert(Image.Format.Rgba8);image.SavePng(Path.Combine(folder,"window-rays-gpu.png"));
            }
            Clear();Check(!pass.Draw(rd,framebuffer,default,default,N2.Zero,size,size,new[]{patch})&&pass.ResourceCount==0,"missing current views fail closed/free");
            Check(baseline.AsSpan().SequenceEqual(Read()),"failure exact pixels");
            pass.Dispose();Check(pass.ResourceCount==0&&pass.BufferBytes==0,"release owns no borrowed targets");
            Check(pass.Draw(rd,framebuffer,default,default,N2.Zero,size,size,ReadOnlySpan<WindowRayPatch>.Empty),"off after failure");
            Check(baseline.AsSpan().SequenceEqual(Read()),"off return exact pixels");
        }
        finally { pass.Dispose();if(framebuffer.IsValid)rd.FreeRid(framebuffer);if(target.IsValid)rd.FreeRid(target);rd.Free(); }
    }
}
