// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;
using GUO.Compat;

namespace GUO.Host;

public partial class LightingSpeedProbe
{
    private async Task CheckCompose(string output)
    {
        bool previous=SpeedModeDebug.Probe;
        using var world=new RenderTarget2D(this,128,128);
        using var composed=new RenderTarget2D(this,128,128);
        using var pixels=Image.CreateEmpty(2,2,false,Image.Format.Rgba8);
        pixels.Fill(Colors.White);
        using var white=ImageTexture.CreateFromImage(pixels);
        using var batcher=new UltimaBatcher2D(composed.CanvasItem) { HueTexture=white };
        var rectangle=new Rectangle(0,0,128,128);
        var hue=new Vector3(0,0,1);
        var dark=new BlendState { ColorSourceBlend=Blend.Zero,ColorDestinationBlend=Blend.SourceColor,ColorBlendFunction=BlendFunction.Add };
        byte[] baseline=null;
        (int TextureSwitches,int Items,int Commands) offCounts=default;
        try
        {
            foreach(string stage in new[] { "off-before","on","failed","world-failed","off-after" })
            {
                bool active=stage=="on" || stage=="failed" || stage=="world-failed";
                SpeedModeDebug.Probe=active;
                OrderedWorldCapture.InjectSpeedLightFailure=stage=="failed";
                OrderedWorldCapture.InjectFailure=stage=="world-failed" ? 2 : 0;
                batcher.BeginFrame();
                if(stage=="on") offCounts=UltimaBatcher2D.LastFrame; // BeginFrame publishes the completed prior frame.
                batcher.SetRenderTarget(world); batcher.Clear(new Godot.Color(0,0,0,1)); batcher.Begin();
                batcher.OrderedSurfaceOrigin=default;
                batcher.BeginOrderedWorld(true);
                if(!batcher.OrderedWorldRecording) throw new InvalidOperationException("Compose fixture failed Depth World admission.");
                batcher.SetOrderedRecovery(()=> { batcher.BeginOrderedRecovery(); batcher.OrderedRecoveryGroup(0,0); batcher.ReplayOrderedGroup(0); });
                batcher.OrderedSurface=new SurfaceRow(0,0,0,0,1,0);
                batcher.Draw(white,rectangle,hue,0);
                batcher.EndOrderedWorld(); batcher.End();
                Texture2D light=null;
                int bound=0,cleared=0;
                if(active)
                    light=batcher.QueueSpeedLight(new SpeedLightFrame { Origin=default,Ambient=new Vector3(0.25f,0.5f,0.75f),RoofShare=1,
                        BindLightTarget=(_,_,_,_)=>bound++,ClearLightTarget=_=>cleared++ });
                batcher.SetRenderTarget(composed); batcher.Clear(new Godot.Color(0,0,0,1)); batcher.Begin();
                batcher.Draw(world,rectangle,hue,0);
                batcher.SetBlendState(dark);
                bool selected=batcher.TryDrawSpeedLight(white,light,rectangle,hue,dark);
                if(selected!=active) throw new InvalidOperationException("Compose branch admission changed.");
                if(!selected) batcher.Draw(white,rectangle,hue,0);
                batcher.SetBlendState(null); batcher.End(); batcher.SetRenderTarget(null);
                await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var image=composed.Texture.GetImage(); image.Convert(Image.Format.Rgba8);
                byte[] actual=image.GetData();
                if((stage=="on" && bound!=1) || (stage=="failed" && (bound!=0 || cleared<1))
                    || (stage=="world-failed" && (bound!=1 || cleared<1 || SpeedModeBindings.Current.LightTarget!=null)))
                    throw new InvalidOperationException("Light consumer fallback lifetime failed: "+stage);
                if(image.SavePng(Path.Combine(output,"compose-"+stage+".png"))!=Error.Ok) throw new IOException("Cannot save compose fixture.");
                if(stage=="off-before") baseline=actual;
                else if(stage=="on") {
                    var colour=image.GetPixel(96,96);
                    if(colour.R<0.20f || colour.R>0.30f || colour.G<0.45f || colour.G>0.55f || colour.B<0.70f || colour.B>0.80f)
                        throw new InvalidOperationException("Speed light compose did not multiply the world: "+colour);
                }
                else if(!baseline.AsSpan().SequenceEqual(actual))
                    throw new InvalidOperationException("Classic fallback or off compose changed pixels: "+stage);
                if(stage=="off-after") {
                    batcher.BeginFrame();
                    if(offCounts!=UltimaBatcher2D.LastFrame)
                        throw new InvalidOperationException($"Off draw counts changed: {offCounts} -> {UltimaBatcher2D.LastFrame}");
                }
            }
            GD.Print("[lighting-speed-probe] COMPOSE PASS multiply=pass injected-light-fallback-diff=0 injected-world-fallback-diff=0 consumer-clear=pass off-diff=0 off-counts="+offCounts);
        }
        finally { OrderedWorldCapture.InjectSpeedLightFailure=false; OrderedWorldCapture.InjectFailure=0; SpeedModeDebug.Probe=previous; }
    }
}
