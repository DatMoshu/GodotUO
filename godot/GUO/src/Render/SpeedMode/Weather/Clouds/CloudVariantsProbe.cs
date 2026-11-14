// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Variant rendering gates, isolated from accounts and owner profiles.</summary>
public partial class CloudVariantsProbe : Node
{
    public override async void _Ready()
    {
        try {
            Require(DisplayServer.GetName()!="headless","Real renderer required");
            string output=System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
            Require(!string.IsNullOrWhiteSpace(output),"Set GUO_WEATHER_PROBE_OUT to external evidence");
            Directory.CreateDirectory(output);
            var viewport=new SubViewport {Size=new Vector2I(960,640),RenderTargetUpdateMode=SubViewport.UpdateMode.Always,
                CanvasItemDefaultTextureFilter=Viewport.DefaultCanvasItemTextureFilter.Nearest};
            AddChild(viewport);
            var background=new ColorRect {Size=viewport.Size,Color=new Color(.12f,.18f,.22f)};
            viewport.AddChild(background);
            await Draw();using var original=viewport.GetTexture().GetImage();
            byte[] previous=null;
            foreach(string name in CloudPresetProbe.Presets) {
                string path=Path.Combine(output,name+".json");File.WriteAllText(path,CloudPresetProbe.Json(name));
                var clouds=new SpeedModeCloudPass();viewport.AddChild(clouds);
                Require(clouds.Configure(path),"Preset selected: "+name);
                var camera=new Vector3(6000,4000,0);
                clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,4,300),1,500);
                await Draw();using(var threshold=viewport.GetTexture().GetImage()) Require(Same(original,threshold),"Exact threshold pixels");
                Require(clouds.Material==null,"Threshold allocates no shader");
                foreach(float zoom in new[]{4.5f,5f,6f}) {
                    clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,zoom,300),1,500);
                    clouds.ProtectPin(zoom>5 ? new Vector2(480,320) : new Vector2(-1000,-1000),zoom>5);
                    await Draw();using var day=viewport.GetTexture().GetImage();
                    Require(!Same(original,day),"Visible "+name+" zoom="+zoom);
                    if(zoom>5) Require(original.GetPixel(480,320)==day.GetPixel(480,320),"Pin protected");
                    day.SavePng(Path.Combine(output,name+"-day-z"+zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
                    if(zoom==5) {
                        if(previous!=null) Require(!previous.AsSpan().SequenceEqual(day.GetData()),"Distinct take");
                        previous=day.GetData();
                        // +5 world-X tiles at zoom5 moves a fixed world point -22,-22px.
                        clouds.Bind(viewport.Size,camera+new Vector3(5,0,0),new SpeedModeCloudView(true,zoom,300),1,500);
                        await Draw();using(var shifted=viewport.GetTexture().GetImage()) {
                            int wrong=0,total=0;
                            for(int y=80;y<240;y++) for(int x=80;x<400;x++) {
                                var a=day.GetPixel(x,y);var b=shifted.GetPixel(x-22,y-22);total++;
                                if(Math.Abs(a.R-b.R)>1.01f/255 || Math.Abs(a.G-b.G)>1.01f/255 || Math.Abs(a.B-b.B)>1.01f/255) wrong++;
                            }
                            Require(wrong<total/100,"World lock drift "+name+": "+wrong+"/"+total);
                        }
                        clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,zoom,300),1,500,0);
                        await Draw();using var night=viewport.GetTexture().GetImage();
                        Require(!Same(day,night),"Night response");night.SavePng(Path.Combine(output,name+"-night.png"));
                        clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,zoom,300),1,510);
                        await Draw();using var moved=viewport.GetTexture().GetImage();Require(!Same(day,moved),"Wind motion");
                        clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,zoom,300),0,500);
                        await Draw();using var calm=viewport.GetTexture().GetImage();
                        clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,zoom,300),0,510);
                        await Draw();using var still=viewport.GetTexture().GetImage();Require(Same(calm,still),"Zero wind stable");
                    }
                }
                clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,4,0),1,500);
                await Draw();using(var off=viewport.GetTexture().GetImage()) Require(Same(original,off),"Exact return");
                clouds.Free();
            }
            // A valid zero-coverage style is explicitly transparent in the shader.
            var zero=System.Text.Json.Nodes.JsonNode.Parse(CloudPresetProbe.Json("thin-haze"));
            zero["layers"][0]["coverage"]=0;
            string zeroPath=Path.Combine(output,"zero.json");File.WriteAllText(zeroPath,zero.ToJsonString());
            var zeroClouds=new SpeedModeCloudPass();viewport.AddChild(zeroClouds);
            Require(zeroClouds.Configure(zeroPath),"Zero coverage selected");
            zeroClouds.Bind(viewport.Size,new Vector3(6000,4000,0),new SpeedModeCloudView(true,5,300),1,500);
            await Draw();using(var empty=viewport.GetTexture().GetImage()) Require(Same(original,empty),"Zero coverage exact pixels");
            zeroClouds.Free();
            zero=System.Text.Json.Nodes.JsonNode.Parse(CloudPresetProbe.Json("soft-billowing"));
            foreach(var layer in zero["layers"].AsArray()) layer["opacity"]=0;
            File.WriteAllText(zeroPath,zero.ToJsonString());
            var invisible=new SpeedModeCloudPass();viewport.AddChild(invisible);
            Require(invisible.Configure(zeroPath),"Zero opacity selected");
            invisible.Bind(viewport.Size,new Vector3(6000,4000,0),new SpeedModeCloudView(true,6,300),1,500);
            await Draw();using(var empty=viewport.GetTexture().GetImage()) Require(Same(original,empty),"Combined zero opacity exact pixels");
            invisible.Free();
            // Editable speed is a multiplier of weather wind, not a calm override.
            async Task<byte[]> WithUserSpeed(float speed,double seconds) {
                var style=System.Text.Json.Nodes.JsonNode.Parse(CloudPresetProbe.Json("thin-haze"));
                style["layers"][0]["wind_speed"]=speed;
                string path=Path.Combine(output,"user-speed.json");File.WriteAllText(path,style.ToJsonString());
                var layer=new SpeedModeCloudPass();viewport.AddChild(layer);
                Require(layer.Configure(path),"User speed style selected");
                layer.Bind(viewport.Size,new Vector3(6000,4000,0),new SpeedModeCloudView(true,5,300),1,seconds);
                await Draw();using var frame=viewport.GetTexture().GetImage();var bytes=frame.GetData();
                layer.Free();return bytes;
            }
            var stoppedA=await WithUserSpeed(0,500);var stoppedB=await WithUserSpeed(0,510);
            Require(stoppedA.AsSpan().SequenceEqual(stoppedB),"JSON speed0 freezes even with weather wind1");
            var speedA=await WithUserSpeed(.1f,500);var speedB=await WithUserSpeed(.2f,500);
            Require(!speedA.AsSpan().SequenceEqual(speedB),"Distinct positive JSON speeds change advection with wind1");
            // Read actual composed alpha without a background obscuring the cap.
            zero=System.Text.Json.Nodes.JsonNode.Parse(CloudPresetProbe.Json("soft-billowing"));
            foreach(var layer in zero["layers"].AsArray()) {
                layer["algorithm"]="procedural";layer["sprite"]="";
                layer["coverage"]=1;layer["density"]=2;layer["opacity"]=1;
            }
            File.WriteAllText(zeroPath,zero.ToJsonString());
            var capped=new SpeedModeCloudPass();viewport.AddChild(capped);
            Require(capped.Configure(zeroPath),"Dense two-layer cap style selected");
            background.Visible=false;viewport.TransparentBg=true;
            foreach(bool overview in new[]{false,true}) {
                capped.Bind(viewport.Size,new Vector3(6000,4000,0),new SpeedModeCloudView(true,overview?6:5,300),1,500);
                capped.ProtectPin(new Vector2(-1000,-1000),overview);
                await Draw();using var image=viewport.GetTexture().GetImage();float maxAlpha=0;
                for(int y=0;y<image.GetHeight();y++) for(int x=0;x<image.GetWidth();x++) maxAlpha=Math.Max(maxAlpha,image.GetPixel(x,y).A);
                float cap=overview ? .32f : .82f;
                Require(maxAlpha<=cap+.005f&&maxAlpha>=cap-.005f,"Post-composition alpha cap "+cap+" actual="+maxAlpha);
            }
            capped.Free();
            GD.Print("[cloud-variants] PASS three distinct takes,4/4.5/5/6,night,wind,calm,world-lock,pin,zero,exact-off");GetTree().Quit();
        }
        catch(Exception e) {GD.PrintErr("[cloud-variants] FAIL "+e);GetTree().Quit(1);}
    }
    private async Task Draw() {for(int i=0;i<2;i++) {await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);}}
    private static bool Same(Image a,Image b)=>a.GetData().AsSpan().SequenceEqual(b.GetData());
    private static void Require(bool value,string message) {if(!value) throw new InvalidOperationException(message);}
}
