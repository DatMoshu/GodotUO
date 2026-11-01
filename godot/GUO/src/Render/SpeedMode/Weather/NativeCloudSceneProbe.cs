// SPDX-License-Identifier: BSD-2-Clause
// Disconnected real GameScene fixture, task1035. Native client art stays local.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Scenes;
using GUO.Network;

namespace GUO.Renderer.SpeedMode.Weather;

public partial class NativeCloudSceneProbe : Node
{
    private string _root, _stills;
    private readonly List<object> _rows=new();
    private readonly List<string> _failures=new();
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private GameScene _scene;
    private RenderTargets _targets;
    private MethodInfo _draw, _resetClouds;
    private GUO.GameController _game;
    private Profile _profile;
    private uint _tick;

    public override void _EnterTree()
    {
        _root=System.Environment.GetEnvironmentVariable("GUO_CLOUD_SCENE_OUT");
        if(string.IsNullOrWhiteSpace(_root)) throw new InvalidOperationException("Set GUO_CLOUD_SCENE_OUT to an external evidence run.");
        _root=Path.GetFullPath(_root);
        _stills=Path.Combine(_root,"stills");
        string scratch=Path.Combine(_root,"data","fixture");
        Directory.CreateDirectory(_stills); Directory.CreateDirectory(scratch);
        // All writable paths are isolated BEFORE the child Main bootstraps.
        GUO.Workspace.Workspace.RootOverride=Path.Combine(scratch,"workspace");
        System.Environment.SetEnvironmentVariable("UO_WORKSPACE_DIR",GUO.Workspace.Workspace.RootOverride);
        System.Environment.SetEnvironmentVariable("UO_CACHE_DIR",Path.Combine(scratch,"cache"));
        Settings.CustomSettingsFilepath=Path.Combine(scratch,"settings.json");
        File.WriteAllText(Settings.CustomSettingsFilepath,JsonSerializer.Serialize(new {
            autologin=false,saveaccount=false,login_music=false,is_win_maximized=false,
            profilespath=Path.Combine(scratch,"profiles"),plugins=Array.Empty<string>() }));
    }

    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    private void Require(bool ok,string reason) { if(!ok) throw new InvalidOperationException(reason); }
    private void Record(bool ok,string reason) { if(!ok) _failures.Add(reason); }
    private static T Read<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private).GetValue(owner);
    private static int PixelDiff(Image a,Image b)
    {
        if(a.GetSize()!=b.GetSize()) return -1;
        var x=a.GetData();var y=b.GetData();int count=0;
        for(int i=0;i<x.Length;i+=4) if(x[i]!=y[i]||x[i+1]!=y[i+1]||x[i+2]!=y[i+2]||x[i+3]!=y[i+3]) count++;
        return count;
    }
    private async Task<Image> Capture(string name,bool resetClouds=false,bool save=true)
    {
        _scene.Camera.Update(true,0,GUO.Input.Mouse.Position);
        _draw.Invoke(_game,null);
        if(resetClouds) _resetClouds.Invoke(_scene,null);
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        var image=_targets.WorldRenderTarget.Texture.GetImage();
        if(save) image.SavePng(Path.Combine(_stills,name+".png"));
        return image;
    }
    private async Task Warm(int count=6)
    {
        for(int i=0;i<count;i++) { using var image=await Capture("warm",save:false); }
    }
    private void Zoom(float zoom)
    {
        _scene.Camera.SetZoomRange(.5f,1024,.1f);
        _scene.Camera.Zoom=zoom;
        _scene.Camera.Update(true,0,GUO.Input.Mouse.Position);
    }
    private bool HasClouds()=>Read<SpeedModeCloudPass>(_scene,"_speedCloudPass")!=null
        && Read<Rid>(_scene,"_speedCloudItem").IsValid;
    private async Task Crossing(float zoom)
    {
        Zoom(zoom); await Warm(zoom>5 ? 40 : 8);
        using var off=await Capture($"zoom-{zoom:0.0}-cloud-reset",true);
        using var on=await Capture($"zoom-{zoom:0.0}-cloud-on");
        int diff=PixelDiff(off,on);
        bool overview=_scene.OverviewActive;
        Record(overview==(zoom>5),"actual overview boundary "+zoom);
        Record(zoom<=4 ? !HasClouds() && diff==0 : HasClouds() && diff>1000,"actual cloud draw at zoom "+zoom+" diff="+diff);
        int pinDiff=0,labelInk=0,labelRetained=0;Vector2 pin=default;
        if(overview) {
            var world=_game.UO.World;var player=world.Player;var maps=_game.UO.FileManager.Maps;
            var offset=Read<GUO.Compat.Point>(_scene,"_offset");
            var transform=WorldOverview.MapTransform(_scene.Camera,offset,player.Z,
                maps.MapBlocksSize[world.Map.Index,0]*8,maps.MapBlocksSize[world.Map.Index,1]*8);
            pin=transform*new Vector2(player.X+.5f,player.Y+.5f);
            // Real pin art: its opaque head centre is 32px ABOVE the ground tip.
            for(int y=-9;y<=9;y++) for(int x=-9;x<=9;x++) {
                if(x*x+y*y>81) continue;
                int px=(int)pin.X+x,py=(int)pin.Y-32+y;
                if(px>=0&&py>=0&&px<on.GetWidth()&&py<on.GetHeight()&&off.GetPixel(px,py)!=on.GetPixel(px,py)) pinDiff++;
            }
            Record(pinDiff==0,"actual opaque overview pin head changed "+pinDiff+"px");
            var bounds=Read<List<Rect2>>(_scene.Overview,"_labelBounds");
            var names=Read<HashSet<string>>(_scene.Overview,"_labelNames");
            Record(names.Contains("Britain")&&bounds.Count>1,"actual overview Britain label rendered");
            if(bounds.Count>1) {
                var label=bounds[1];
                var rect=new Rect2I((int)label.Position.X,(int)label.Position.Y,(int)label.Size.X,(int)label.Size.Y);
                using var crop=on.GetRegion(rect);crop.SavePng(Path.Combine(_stills,"actual-britain-label-on.png"));
                using var before=off.GetRegion(rect);before.SavePng(Path.Combine(_stills,"actual-britain-label-reset.png"));
                for(int y=3;y<rect.Size.Y-2;y++) for(int x=4;x<rect.Size.X-4;x++) {
                    var a=before.GetPixel(x,y);var b=crop.GetPixel(x,y);
                    float l=(a.R+a.G+a.B)/3;
                    if(l>.55f) { labelInk++;if((b.R+b.G+b.B)/3>=l*.65f) labelRetained++; }
                }
                Record(labelInk>5&&labelRetained>=labelInk*.95,"actual label ink contrast retained");
            }
        }
        _rows.Add(new { zoom,overview,clouds=HasClouds(),changedPixels=diff,pinX=pin.X,pinY=pin.Y,
            opaquePinHeadChanged=pinDiff,labelInk,labelRetained,objects=_scene.RenderedObjectsCount });
        SaveResults();
    }
    private void SaveResults()=>File.WriteAllText(Path.Combine(_root,"data","native-cloud-results.json"),
        JsonSerializer.Serialize(new {disconnected=true,rows=_rows,failures=_failures},new JsonSerializerOptions{WriteIndented=true}));

    public override async void _Ready()
    {
        var oldCities=WorldOverview.Cities;
        try {
            Require(DisplayServer.GetName()!="headless","Actual scene proof requires a real renderer");
            for(int i=0;Client.Game?.Scene is not LoginScene;i++) {
                if(i>1200) throw new InvalidOperationException("client login-scene bootstrap timeout"); await Frame();
            }
            _game=Client.Game;var world=_game.UO.World;
            Require(!NetClient.Socket.IsConnected,"fixture starts disconnected");
            // Same unconnected transport initialization as NativeMobileShadowProbe.
            // Never Connect, bind a listener, send a login, or edit network source.
            var setup=typeof(NetClient).GetMethod("SetupSocket",Private);
            setup.Invoke(NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
            Settings.GlobalSettings.ProfilesPath=Path.Combine(_root,"data","fixture","profiles");
            ProfileManager.Load("Offline","cloud-fixture","cloud-fixture");
            _profile=ProfileManager.CurrentProfile;
            _profile.GameWindowFullSize=true;_profile.TopbarGumpIsDisabled=true;
            _profile.UseCircleOfTransparency=false;_profile.NoColorObjectsOutOfRange=false;
            _profile.EnableDeathScreen=false;_profile.GameWindowSize=new GUO.Compat.Point(1280,720);
            _profile.ExperimentalWorldOverview=true;_profile.LightingSpeedMode=true;
            _profile.SpeedModeSunShadows=false;_profile.SpeedModeLampShadows=0;_profile.SpeedModeWindowLight=false;
            _profile.SpeedModeWeather=false;_profile.SpeedModeFog=false;_profile.SpeedModeReflections=false;
            _profile.WaterStyle="classic";_profile.WindStrength=0;
            world.CreatePlayer(1);world.MapIndex=0;
            world.Player.Graphic=0x190;world.Player.Name="Disconnected cloud fixture";
            world.Player.SetInWorldTile(1431,1691,20);
            // Fixture metadata through the ACTUAL normal city-label renderer.
            WorldOverview.Cities=new[]{new CityInfo(0,"Britain","","Disconnected fixture marker",1496,1628,10,0,true)};
            _scene=new GameScene(world);_game.SetScene(_scene);
            for(int i=0;i<12;i++) await Frame();
            _game.SetProcess(false);_tick=Time.Ticks;Time.Ticks=500000;
            _draw=_game.GetType().GetMethod("DrawFrame",Private);
            _targets=Read<RenderTargets>(_game,"_renderTargets");
            _resetClouds=typeof(GameScene).GetMethod("ResetSpeedClouds",Private);
            foreach(float zoom in new[]{4f,4.5f,5f,6f}) await Crossing(zoom);

            // Sample several real cloud phases over the actual opaque pin head;
            // a lucky gap in one procedural frame must not pass clearance.
            _profile.WindStrength=1;
            for(int phase=0;phase<6;phase++) {
                Time.Ticks=(uint)(500000+phase*90000);Zoom(6);await Warm(2);
                using var pinOff=await Capture("pin-phase-"+phase+"-reset",true);
                using var pinOn=await Capture("pin-phase-"+phase+"-cloud");
                var w=world;var m=_game.UO.FileManager.Maps;
                var transform=WorldOverview.MapTransform(_scene.Camera,Read<GUO.Compat.Point>(_scene,"_offset"),w.Player.Z,
                    m.MapBlocksSize[w.Map.Index,0]*8,m.MapBlocksSize[w.Map.Index,1]*8);
                var pin=transform*new Vector2(w.Player.X+.5f,w.Player.Y+.5f);int changed=0;
                for(int py=-9;py<=9;py++) for(int px=-9;px<=9;px++) {
                    if(px*px+py*py>81) continue;
                    int x=(int)pin.X+px,y=(int)pin.Y-32+py;
                    if(x>=0&&y>=0&&x<pinOn.GetWidth()&&y<pinOn.GetHeight()&&pinOff.GetPixel(x,y)!=pinOn.GetPixel(x,y)) changed++;
                }
                Record(changed==0,"actual pin head phase"+phase+" changed="+changed);
                _rows.Add(new{test="pin-phase",phase,changedPixels=changed});
            }
            Time.Ticks=500000;_profile.WindStrength=0;

            // Main parent-off exact restoration at fixed native camera.
            Zoom(4.5f);_profile.LightingSpeedMode=false;await Warm();
            using var parentBefore=await Capture("parent-off-before");
            _profile.LightingSpeedMode=true;await Warm();using var parentOn=await Capture("parent-on");
            _profile.LightingSpeedMode=false;await Warm();using var parentAfter=await Capture("parent-off-return");
            int parentDiff=PixelDiff(parentBefore,parentAfter);
            Record(parentDiff==0&&!HasClouds(),"exact parent-off native return diff="+parentDiff);
            _rows.Add(new{test="parent-off-return",changedPixels=parentDiff});

            // Experimental-off legitimately clamps native zoom to2.5; compare matching views.
            _profile.LightingSpeedMode=true;_profile.ExperimentalWorldOverview=false;Zoom(2.5f);await Warm();
            using var experimentBefore=await Capture("experimental-off-before");
            _profile.ExperimentalWorldOverview=true;Zoom(6);await Warm(12);using var experimentOn=await Capture("experimental-on");
            _profile.ExperimentalWorldOverview=false;await Warm();using var experimentAfter=await Capture("experimental-off-return");
            int experimentDiff=PixelDiff(experimentBefore,experimentAfter);
            Record(experimentDiff==0&&!HasClouds()&&_scene.Camera.Zoom==2.5f,"exact experimental-off return diff="+experimentDiff);
            _rows.Add(new{test="experimental-off-return",changedPixels=experimentDiff,zoom=_scene.Camera.Zoom});

            _profile.ExperimentalWorldOverview=true;Zoom(5);await Warm();
            _profile.WindStrength=0;using var calm=await Capture("wind-calm");
            _profile.WindStrength=1;using var windy=await Capture("wind-one");
            int windDiff=PixelDiff(calm,windy);Record(windDiff>1000,"actual adapter wind moves cloud field");
            _rows.Add(new{test="wind",changedPixels=windDiff});

            var oldTarget=_targets.WorldRenderTarget;var oldPass=Read<SpeedModeCloudPass>(_scene,"_speedCloudPass");
            DisplayServer.WindowSetSize(new Vector2I(960,640));await Frame();
            _scene.Camera.Bounds.Width=960;_scene.Camera.Bounds.Height=640;
            _profile.GameWindowSize=new GUO.Compat.Point(960,640);Zoom(5);await Warm();
            using var resized=await Capture("resized-native-cloud");
            Record(oldTarget.IsDisposed&&!ReferenceEquals(oldTarget,_targets.WorldRenderTarget),"actual resize retires world target");
            Record(!GodotObject.IsInstanceValid(oldPass)&&HasClouds(),"resize releases old cloud node and creates current target owner");
            Record(resized.GetWidth()==960&&resized.GetHeight()==640,"resize uses actual new target dimensions");
            _rows.Add(new{test="resize",width=resized.GetWidth(),height=resized.GetHeight()});

            var lastPass=Read<SpeedModeCloudPass>(_scene,"_speedCloudPass");
            _game.SetScene(new LoginScene(world));
            Record(_scene.IsDestroyed&&Read<SpeedModeCloudPass>(_scene,"_speedCloudPass")==null
                &&!Read<Rid>(_scene,"_speedCloudItem").IsValid&&!GodotObject.IsInstanceValid(lastPass),"actual scene unload clears cloud node/material/RID");
            // Task1042: a normal rendered unload must retire the producer too.
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            Record(SpeedModeBindings.Current.Surface==null&&SpeedModeBindings.Current.LightTarget==null,
                "rendered unload clears core surface/light publication");
            var capture=Read<GUO.Renderer.UltimaBatcher2D>(_game,"_uoSpriteBatch").OrderedWorldProbe;
            var device=Read<GUO.Renderer.DepthWorld.OrderedWorldDevice>(capture,"_device");
            Record(!device.SurfaceReady&&!device.SpeedLightReady,"rendered unload releases optional producer targets");
            _rows.Add(new{test="producer-unload",surfaceRemaining=SpeedModeBindings.Current.Surface!=null,
                lightRemaining=SpeedModeBindings.Current.LightTarget!=null,surfaceReady=device.SurfaceReady,lightReady=device.SpeedLightReady});

            // Reuse the same batcher, then unload BEFORE its next FramePreDraw.
            // This is the stale-publication race a clear-only hook would miss.
            ProfileManager.Load("Offline","cloud-fixture","cloud-fixture");_profile=ProfileManager.CurrentProfile;
            _profile.LightingSpeedMode=true;_profile.ExperimentalWorldOverview=true;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;
            world.Player.Name="Disconnected retirement fixture";world.Player.SetInWorldTile(1431,1691,20);
            _scene=new GameScene(world);_game.SetScene(_scene);_game.SetProcess(false);Zoom(4);
            await Warm(8);
            Record(SpeedModeBindings.Current.Surface!=null&&SpeedModeBindings.Current.LightTarget!=null,
                "new scene reuses producer after retirement");
            _draw.Invoke(_game,null); // capture queued, not yet submitted to FramePreDraw
            Record(!capture.CanBegin,"queued fixture ticket exists before unload");
            _game.SetScene(new LoginScene(world));
            Record(SpeedModeBindings.Current.Surface==null&&SpeedModeBindings.Current.LightTarget==null,
                "queued unload clears publication immediately");
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            Record(SpeedModeBindings.Current.Surface==null&&SpeedModeBindings.Current.LightTarget==null,
                "queued old frame cannot republish after unload");
            Record(capture.CanBegin&&!device.SurfaceReady&&!device.SpeedLightReady,
                "queued retirement completes and next scene may begin");
            _rows.Add(new{test="queued-unload",canBegin=capture.CanBegin,
                surfaceRemaining=SpeedModeBindings.Current.Surface!=null,lightRemaining=SpeedModeBindings.Current.LightTarget!=null});
            Require(!NetClient.Socket.IsConnected,"fixture stayed disconnected throughout");
            _rows.Add(new{test="unload",destroyed=_scene.IsDestroyed,disconnected=!NetClient.Socket.IsConnected});
            SaveResults();Require(_failures.Count==0,string.Join("; ",_failures));
            GD.Print("[native-cloud-scene] PASS disconnected=1 actual-GameScene=1 native4/4.5/5-overview6=pass pin/label=pass parent/experimental-off-diff=0 wind/resize/unload=pass");
            GetTree().Quit(0);
        }
        catch(Exception error) {
            _failures.Add(error.ToString());SaveResults();
            GD.PrintErr("[native-cloud-scene] FAIL "+error);GetTree().Quit(1);
        }
        finally { WorldOverview.Cities=oldCities;if(_tick!=0) Time.Ticks=_tick; }
    }
}
