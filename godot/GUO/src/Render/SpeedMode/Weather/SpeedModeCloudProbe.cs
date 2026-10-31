// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Synthetic rendered cloud entry/height/motion and exact-off fixture.</summary>
public partial class SpeedModeCloudProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            Require(DisplayServer.GetName() != "headless", "Cloud proof requires a real renderer");
            var viewport = new SubViewport { Size = new Vector2I(1280,720),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest };
            AddChild(viewport);
            var background = new ColorRect { Size=viewport.Size, Color=new Color(0.12f,0.18f,0.22f) };
            viewport.AddChild(background);
            var clouds = new SpeedModeCloudPass(); viewport.AddChild(clouds);
            var camera = new Vector3(6000,4000,0);
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(false,5,300),1,0);
            await Draw(); using var original=viewport.GetTexture().GetImage();
            Require(clouds.Material==null,"Disabled view must allocate no material");
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,4,300),1,0);
            await Draw(); using var threshold=viewport.GetTexture().GetImage();
            Require(Same(original,threshold),"Zoom4 must remain exact original pixels");
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,5,40),1,0);
            await Draw(); using var below=viewport.GetTexture().GetImage();
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,5,300),1,0);
            await Draw(); using var above=viewport.GetTexture().GetImage();
            Require(!Same(below,above),"Height changes cloud-plane parallax and upper lighting");
            int changed=0;
            for(int y=0;y<720;y++) for(int x=0;x<1280;x++)
                if(original.GetPixel(x,y)!=above.GetPixel(x,y)) changed++;
            Require(changed>100000,"Clouds must visibly cover part of the world");
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,5,300),1,0,0.1f);
            await Draw(); using var night=viewport.GetTexture().GetImage();
            int darker=0;
            for(int y=0;y<720;y++) for(int x=0;x<1280;x++)
                if(night.GetPixel(x,y).R+0.02f<above.GetPixel(x,y).R) darker++;
            Require(darker>1000,"Night daylight must darken the cloud plane");
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,5,300),1,10);
            await Draw(); using var moved=viewport.GetTexture().GetImage();
            Require(!Same(above,moved),"Wind/time advects clouds");
            clouds.Bind(viewport.Size,camera,new SpeedModeCloudView(true,6,300),1,10);
            clouds.ProtectPin(new Vector2(640,360),true);
            await Draw(); using var overview=viewport.GetTexture().GetImage();
            Require(!Same(original,overview),"Overview beyond5 retains clouds without any surface or light target");
            Require(original.GetPixel(640,360)==overview.GetPixel(640,360),"Overview player pin stays unobscured");
            clouds.HideForFallback();
            await Draw(); using var off=viewport.GetTexture().GetImage();
            Require(Same(original,off),"Cloud disable/fallback restores every original pixel");
            string output=System.Environment.GetEnvironmentVariable("GUO_WEATHER_PROBE_OUT");
            if(!string.IsNullOrEmpty(output)) {
                Directory.CreateDirectory(output);
                below.SavePng(Path.Combine(output,"guo_1026_cloud_below_20261030.png"));
                above.SavePng(Path.Combine(output,"guo_1026_cloud_above_20261030.png"));
                overview.SavePng(Path.Combine(output,"guo_1026_cloud_overview_20261030.png"));
                off.SavePng(Path.Combine(output,"guo_1026_cloud_off_20261030.png"));
            }
            GD.Print($"[speed-cloud] PASS: {changed} changed pixels; guarded entry above4, height, wind, exact off");
            GetTree().Quit();
        }
        catch(Exception error) { GD.PrintErr("[speed-cloud] FAIL: "+error); GetTree().Quit(1); }
    }
    private async System.Threading.Tasks.Task Draw()
    {
        for(int i=0;i<2;i++) { await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw); }
    }
    private static bool Same(Image a,Image b)=>a.GetData().AsSpan().SequenceEqual(b.GetData());
    private static void Require(bool test,string message) { if(!test) throw new InvalidOperationException(message); }
}
