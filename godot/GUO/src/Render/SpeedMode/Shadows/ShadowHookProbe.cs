// SPDX-License-Identifier: BSD-2-Clause
// Rendered callback/alpha/per-lamp lifetime regression; synthetic art only.
using System;
using System.IO;
using Godot;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.Lights;
using N3=System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class ShadowHookProbe : Node
{
    public override async void _Ready()
    {
        int exit=1;
        var device=new OrderedWorldDevice();
        using var image=Image.CreateEmpty(2,2,false,Image.Format.Rgba8);
        image.Fill(Colors.White); image.SetPixel(0,0,new Color(0,0,0,0));
        using var art=ImageTexture.CreateFromImage(image);
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        Exception failure=null;
        RenderingServer.CallOnRenderThread(Callable.From(()=> {
            try {
                device.InitializeRT(new Vector2I(128,128),OrderedWorldCapture.ShaderSource("resident_world_vertex.glsl.txt"),
                    OrderedWorldCapture.ShaderSource("ordered_world_fragment.glsl.txt"),OrderedWorldCapture.ShaderSource("depth_world_resident_vertex.glsl.txt"));
                var resident=new DepthWorldResident();
                var textures=new OrderedWorldDevice.TextureSet(new OrderedWorldDevice.Textures(art.GetRid(),art.GetRid(),art.GetRid()));
                var hue=UltimaBatcher2D.Encode(new Vector3(0,0,1));
                OrderedWorldDevice.Vertex V(float x,float y,float u,float v)=>new() {
                    PositionUv=new(x,y,u,v),PackedHue=new(hue.R,hue.G,hue.B,hue.A),Normal=new(0,0,1,0) };
                var row=new SurfaceRow(0,0,0,0,1);
                resident.Quads[0]=new DepthWorldResident.Quad { A=V(0,0,0,0),B=V(128,0,1,0),C=V(0,128,0,1),D=V(128,128,1,1),
                    Aux=new OrderedWorldDevice.Float4(1.77f,0,0,BitConverter.UInt32BitsToSingle(row.Data)) };
                resident.Merged[0]=0; resident.QuadCount=resident.MergedCount=1;
                resident.Draws.Add(new DepthWorldResident.Draw { Count=1,Land=false,Textures=textures });
                var push=new OrderedWorldDevice.Push { Row0=new Vector4(1,0,0,1),Row1=new Vector4(0,1,0,0),Screen=new Vector4(128,128,0,0) };
                device.RenderRT(Array.Empty<OrderedWorldResidency.Occurrence>(),Array.Empty<OrderedWorldResidency.Quad>(),
                    Array.Empty<OrderedWorldDevice.State>(),Array.Empty<OrderedWorldDevice.Segment>(),0,push,true,resident);
                device.RenderSurfaceRT(Array.Empty<SurfaceRow>(),Array.Empty<OrderedWorldDevice.Segment>(),push,resident,0,0,127,0,default);
                var field=new RealtimeLightField(); field.Begin(-16,-16,32,32); field.BeginSpeedReach();
                var source=new RealtimeLightField.Source(2,0,-10,16,Colors.White);
                var frame=new SpeedLightFrame { Count=1,Ambient=new Vector3(.1f,.1f,.1f),RoofShare=1 };
                frame.Lights[0]=new SpeedLightFrame.Entry { Source=source,Reach=field.GetSpeedReach(in source),
                    PixelCentre=new Vector2(64,64),PixelRadius=256,Flicker=1,ShadowRow=-1 };
                device.RenderLightsRT(frame); byte[] before=device.ReadSpeedLightRT();
                var inputs=new SpeedShadowInputs { Requested=1,LampCount=1,SunEnabled=true,Viewer=N3.Zero };
                inputs.Grid.Begin(-16,-16,32,32); inputs.Lamps[0]=new ShadowLamp(new N3(2.5f,.5f,0),16,0);
                // Full-view stamp with one transparent quadrant. The witness at
                // (96,96) is opaque, (32,32) remains transparent in both passes.
                var stamp=new SpeedShadowInputs.Stamp(art,new Vector2(0,0),new Vector2(128,0),new Vector2(0,128),
                    new Vector2(.25f,.25f),new Vector2(.75f,.25f),new Vector2(.25f,.75f),.4f,0,0);
                inputs.Sun.Add(stamp); inputs.Mobile[0]=new() { stamp };
                int releases=0;
                frame.RenderInputs=inputs.Render;
                frame.ReleaseInputs=()=> { inputs.Release(); releases++; };
                device.RenderLightsRT(frame); byte[] after=device.ReadSpeedLightRT();
                float Read(byte[] b,int x,int y)=>(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b,(y*128+x)*8));
                Check(frame.SunMask.IsValid && frame.LampShadowAtlas.IsValid && frame.Lights[0].MobileShadow.IsValid,"callback publishes real masks");
                Check(Math.Abs(Read(after,96,96)/Read(before,96,96)-.6f)<.01f,$"selected lamp and sun receive matching native-alpha attenuation before={Read(before,96,96)} after={Read(after,96,96)} ratio={Read(after,96,96)/Read(before,96,96)}");
                Check(Math.Abs(Read(after,32,32)-Read(before,32,32))<.001f,"transparent native alpha remains clear");
                string folder=System.Environment.GetEnvironmentVariable("GUO_SHADOW_PROBE_OUT");
                if(!string.IsNullOrEmpty(folder)) {
                    byte[] rgba=new byte[128*128*4];
                    for(int i=0;i<128*128;i++) { byte c=(byte)(Math.Clamp((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(after,i*8)),0,1)*255);
                        rgba[i*4]=rgba[i*4+1]=rgba[i*4+2]=c; rgba[i*4+3]=255; }
                    using var result=Image.CreateFromData(128,128,false,Image.Format.Rgba8,rgba);
                    result.SavePng(Path.Combine(folder,"shadow-hook-light-gpu.png"));
                }
                inputs.Requested=0; inputs.SunEnabled=false;
                device.RenderLightsRT(frame);
                Check(!frame.SunMask.IsValid && !frame.LampShadowAtlas.IsValid && !frame.Lights[0].MobileShadow.IsValid,"disabled passes expose no stale rows");
                Check(before.AsSpan().SequenceEqual(device.ReadSpeedLightRT()),"disabled optional terms restore the base light target");
                device.DisableSurfaceRT();
                Check(releases==1 && !device.HasSurfaceResources,"off releases optional resources exactly once");
                GD.Print("[shadow-hook-probe] PASS real-callback=pass alpha=pass selected-lamp=pass disabled-diff=0 off-release=1");
            }
            catch(Exception e) { failure=e; }
            finally { device.DisposeRT(); }
        }));
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        if(failure==null) exit=0; else GD.PrintErr("[shadow-hook-probe] FAIL "+failure);
        device.Presenter.Dispose(); device.SurfaceTexture.Dispose(); device.SpeedLightTexture.Dispose();
        GetTree().Quit(exit);
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
}
