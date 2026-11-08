// SPDX-License-Identifier: BSD-2-Clause
// Task1043: disconnected actual GameScene/native-art weather qualification.
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
using GUO.Renderer.DepthWorld;
using GUO.Renderer.Water;

namespace GUO.Renderer.SpeedMode.Weather;

public partial class NativeWeatherSceneProbe : Node
{
    private string _root, _stills;
    private readonly List<object> _rows=new();
    private readonly List<string> _failures=new();
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private GameScene _scene;
    private RenderTargets _targets;
    private GUO.GameController _game;
    private Profile _profile;
    private MethodInfo _draw;
    private uint _savedTick;
    public override void _EnterTree()
    {
        _root=System.Environment.GetEnvironmentVariable("GUO_WEATHER_SCENE_OUT");
        if(string.IsNullOrWhiteSpace(_root)) throw new InvalidOperationException("Set GUO_WEATHER_SCENE_OUT to an external evidence run.");
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
    private static T Read<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private).GetValue(owner);
    private static void Require(bool ok,string reason) { if(!ok) throw new InvalidOperationException(reason); }
    private void Record(bool ok,string reason) { if(!ok) _failures.Add(reason); }
    private SpeedModeWeatherRuntime Runtime=>Read<SpeedModeWeatherRuntime>(_scene,"_speedWeatherRuntime");
    private void Save()=>File.WriteAllText(Path.Combine(_root,"data","native-water-witness-results.json"),
        JsonSerializer.Serialize(new {disconnected=!NetClient.Socket.IsConnected,rows=_rows,failures=_failures},
            new JsonSerializerOptions{WriteIndented=true}));
    private async Task<Image> Capture(string name,bool save=true)
    {
        _scene.Camera.Update(true,0,GUO.Input.Mouse.Position);
        _draw.Invoke(_game,null);
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        var image=_targets.WorldRenderTarget.Texture.GetImage();
        if(save) image.SavePng(Path.Combine(_stills,name+".png"));
        return image;
    }
    private (int Objects,int Quads,int Group) AdmissionCounts(GUO.Game.GameObjects.Item donor)
    {
        var lists=Read<RenderLists>(_scene,"_renderLists");
        var objects=Read<System.Collections.IEnumerable>(lists,"_world");
        int count=0,group=-1;
        foreach(var entry in objects) {
            var type=entry.GetType();
            var obj=type.GetField("Object",BindingFlags.Instance|BindingFlags.Public).GetValue(entry);
            if(!ReferenceEquals(obj,donor)) continue;
            count++;group=(int)type.GetField("Seq",BindingFlags.Instance|BindingFlags.Public).GetValue(entry)+1;
        }
        var batcher=Read<GUO.Renderer.UltimaBatcher2D>(_game,"_uoSpriteBatch");
        var commands=Read<List<OrderedWorldCanvas.Command>>(batcher.OrderedWorldProbe,"_commands");
        int quads=0;
        if(group>=0) foreach(var command in commands) if(command.Group==group)quads++;
        return(count,quads,group);
    }
    private int ChangedNativeDonorFootprint(Image negative,Image admitted,Vector2I origin)
    {
        Require(negative.GetSize()==admitted.GetSize(),"stable native surface size across admission");int changed=0;
        for(int y=0;y<admitted.GetHeight();y++)for(int x=0;x<admitted.GetWidth();x++) {
            var p=admitted.GetPixel(x,y);int kind=(int)MathF.Round(p.A)&15;
            if(kind!=2&&kind!=3&&kind!=4)continue;
            float wx=p.R+origin.X,wy=p.G+origin.Y;
            if(wx<NativeWaterWitness.DonorX-.05f||wx>NativeWaterWitness.DonorX+1.05f||
               wy<NativeWaterWitness.DonorY-.05f||wy>NativeWaterWitness.DonorY+1.05f)continue;
            if(p!=negative.GetPixel(x,y))changed++;
        }
        return changed;
    }
    private async Task Warm(int frames=6) { for(int i=0;i<frames;i++) { using var image=await Capture("warm",false); } }
    private static int Diff(Image a,Image b)
    {
        if(a.GetSize()!=b.GetSize()) return -1;
        var x=a.GetData();var y=b.GetData();int n=0;
        for(int i=0;i<x.Length;i+=4) if(x[i]!=y[i]||x[i+1]!=y[i+1]||x[i+2]!=y[i+2]||x[i+3]!=y[i+3]) n++;
        return n;
    }
    private void Packet(WeatherType type,byte count)
    {
        // Actual existing packet parser/0x65 handler, on an unconnected transport.
        byte[] bytes={0x65,(byte)type,count,15};
        PacketHandlers.Handler.Append(bytes,true);
        int packets=PacketHandlers.Handler.ParsePackets(NetClient.Socket,_game.UO.World,Span<byte>.Empty);
        Require(packets==1,"one real framed weather packet dispatched");
        Record(_game.UO.World.Weather.Count==count,"packet count retained");
        _rows.Add(new{test="weather-packet",type=(byte)type,count,packets});
    }
    private void Bound(string name)
    {
        var frame=SpeedModeBindings.Current;var runtime=Runtime;
        Require(runtime!=null&&runtime.Bound&&runtime.BoundSequence==frame.Sequence&&frame.Sequence>0
            &&frame.Surface!=null&&frame.LightTarget!=null,"actual same-frame publication at "+name);
        _rows.Add(new{test="publication",name,tick=Time.Ticks,sequence=runtime.BoundSequence,
            width=frame.Surface.GetWidth(),height=frame.Surface.GetHeight(),originX=frame.Origin.X,originY=frame.Origin.Y});
    }
    private async Task<int[]> Surface(string name)
    {
        Bound(name);
        // Read the actual published native-art geometry target; never author a receiver buffer.
        using var surface=SpeedModeBindings.Current.Surface.GetImage();
        Require(surface!=null&&surface.GetSize()==new Vector2I(_targets.WorldRenderTarget.Width,_targets.WorldRenderTarget.Height),
            "native surface readback size");
        File.WriteAllBytes(Path.Combine(_root,"data",name+"-surface.bin"),surface.GetData());
        int[] flags=new int[surface.GetWidth()*surface.GetHeight()];
        int paving=0,otherFloor=0,water=0,covered=0;
        using var preview=Image.CreateEmpty(surface.GetWidth(),surface.GetHeight(),false,Image.Format.Rgba8);
        for(int y=0;y<surface.GetHeight();y++) for(int x=0;x<surface.GetWidth();x++) {
            int data=(int)MathF.Round(surface.GetPixel(x,y).A),kind=data&15;
            flags[y*surface.GetWidth()+x]=data;
            bool roof=(data&16)!=0,allow=(data&32)!=0;
            if(roof) covered++;
            if(kind==1&&!roof) { if(allow) paving++;else otherFloor++; }
            if(kind==6&&!roof) water++;
            preview.SetPixel(x,y,roof?Colors.Magenta:kind==6?Colors.Cyan:kind==1?(allow?Colors.Yellow:Colors.Green):Colors.Black);
        }
        preview.SavePng(Path.Combine(_stills,name+"-native-surface-classes.png"));
        Record(otherFloor>100,"actual nonallowlisted floor witness present at "+name);
        _rows.Add(new{test="actual-surface",name,format=surface.GetFormat().ToString(),paving,otherFloor,water,covered,
            playerX=_game.UO.World.Player.X,playerY=_game.UO.World.Player.Y,zoom=_scene.Camera.Zoom});
        Save();await Task.CompletedTask;return flags;
    }
    private (int paving,int water,int rejected,int floor) ReflectionDiff(Image before,Image after,int[] surface,string name,bool dry)
    {
        var a=before.GetData();var b=after.GetData();int paving=0,water=0,rejected=0,floor=0;
        Require(a.Length==b.Length&&surface.Length*4==a.Length,"matching reflection and native surface pixels");
        for(int p=0;p<surface.Length;p++) {
            int i=p*4;if(a[i]==b[i]&&a[i+1]==b[i+1]&&a[i+2]==b[i+2]&&a[i+3]==b[i+3]) continue;
            int data=surface[p],kind=data&15;
            if(kind==1) floor++;
            if((data&16)==0&&kind==6) water++;
            else if((data&16)==0&&kind==1&&(data&32)!=0&&!dry) paving++;
            else rejected++;
        }
        Record(rejected==0,"reflection rejected dry/nonallowlisted/covered/nonreceiver pixels at "+name+": "+rejected);
        _rows.Add(new{test="reflection-diff",name,dry,paving,water,rejected,floor});Save();
        return(paving,water,rejected,floor);
    }
    public override async void _Ready()
    {
        NativeWaterWitness witness=null;
        try {
            Require(DisplayServer.GetName()!="headless","Native weather proof requires an actual renderer");
            for(int i=0;Client.Game?.Scene is not LoginScene;i++) {
                if(i>1200) throw new InvalidOperationException("login bootstrap timeout");await Frame();
            }
            _game=Client.Game;var world=_game.UO.World;
            Require(!NetClient.Socket.IsConnected,"starts disconnected");
            var setup=typeof(NetClient).GetMethod("SetupSocket",Private);
            setup.Invoke(NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
            Settings.GlobalSettings.ProfilesPath=Path.Combine(_root,"data","fixture","profiles");
            ProfileManager.Load("Offline","weather-fixture","weather-fixture");_profile=ProfileManager.CurrentProfile;
            _profile.GameWindowFullSize=true;_profile.TopbarGumpIsDisabled=true;
            _profile.UseCircleOfTransparency=false;_profile.NoColorObjectsOutOfRange=false;_profile.EnableDeathScreen=false;
            _profile.GameWindowSize=new GUO.Compat.Point(1280,720);
            _profile.LightingSpeedMode=true;_profile.ExperimentalWorldOverview=true;
            _profile.SpeedModeSunShadows=false;_profile.SpeedModeLampShadows=0;_profile.SpeedModeWindowLight=false;
            _profile.SpeedModeWeather=false;_profile.SpeedModeFog=false;_profile.SpeedModeMorningHaze=0;
            _profile.SpeedModeReflections=false;_profile.WaterStyle="classic";_profile.WindStrength=0;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;
            world.Player.Name="Disconnected weather fixture";world.Player.SetInWorldTile(1431,1691,20);
            _scene=new GameScene(world);_game.SetScene(_scene);
            for(int i=0;i<12;i++) await Frame();
            _game.SetProcess(false);_savedTick=Time.Ticks;Time.Ticks=500000;
            _scene.Camera.SetZoomRange(.5f,1024,.1f);_scene.Camera.Zoom=4;
            _draw=_game.GetType().GetMethod("DrawFrame",Private);_targets=Read<RenderTargets>(_game,"_renderTargets");
            await Warm(12);Bound("dry-start");

            Packet(WeatherType.WT_RAIN,70);await Warm();
            using(var classicRain=await Capture("classic-rain-native")) {
                _profile.SpeedModeWeather=true;await Warm();
                using var rain=await Capture("speed-rain-native");Bound("rain");
                int changed=Diff(classicRain,rain);Record(changed>100,"actual rain sheet changes native frame");
                Record(Read<SpeedModeRainPass>(Runtime,"_rain").Active,"actual rain runtime active");
                _rows.Add(new{test="rain",changedPixels=changed,intensity=SpeedModeWeather.Observe(world,_profile).RainIntensity});
            }
            _profile.SpeedModeFog=false;using(var clear=await Capture("rain-fog-off")) {
                _profile.SpeedModeFog=true;await Warm();using var fog=await Capture("rain-fog-on");Bound("fog");
                int changed=Diff(clear,fog);Record(changed>100,"actual weather mist changes native frame");
                _rows.Add(new{test="fog",changedPixels=changed,density=SpeedModeWeather.Observe(world,_profile).FogDensity});
            }
            _profile.SpeedModeFog=false;
            // Integrate retained per-world weather through its real 60-second wetting interval.
            Time.Ticks+=60000;await Warm();
            Record(SpeedModeWeather.Observe(world,_profile).Wetness>.99f,"real weather wets world in60seconds");
            Packet(WeatherType.WT_INVALID_0,0);await Warm();
            Record(SpeedModeWeather.Observe(world,_profile).Wetness>.99f,"wet ground persists after clear packet");

            // Preserve the original real-bank wet paving witness; no additional map search.
            _profile.SpeedModeReflections=false;await Warm();
            using(var before=await Capture("bank-wet-reflections-off")) {
                var flags=await Surface("bank-wet");_profile.SpeedModeReflections=true;await Warm();
                using var after=await Capture("bank-wet-reflections-on");Bound("bank-wet");
                var diff=ReflectionDiff(before,after,flags,"bank-wet",false);
                Record(diff.paving>0,"actual allowlisted wet paving positive witness");
            }
            // One deterministic fixture at existing water; three approved native
            // donor choices maximum, ordinary coordinates and native GameScene only.
            world.Player.SetInWorldTile(1443,1741,NativeWaterWitness.WaterZ);
            _scene.Camera.Zoom=1;await Warm(24);
            witness=new NativeWaterWitness(world);
            Require(witness.ExistingWater(),"fixed fixture uses existing native wet land/static at expected Z");
            _rows.Add(new{test="native-water-receiver",metadata=witness.ReceiverMetadata});
            using(var existing=await Capture("native-water-existing-before-donor")) { Bound("native-water-existing"); }
            // Chief21593: SAME0080/same coordinate, ONE run, earliest failed count stops it.
            {
            _profile.UseObjectsFading=true;
            object metadata=witness.Install(0x0080);Require(witness.HasDonor,"same0080 donor installed normally");
            _profile.SpeedModeReflections=false;
            using var negative=await Capture("alpha-frozen-negative");
            var negativeCounts=AdmissionCounts(witness.Donor);
            _rows.Add(new{test="alpha-negative",tick=Time.Ticks,alpha=witness.Donor.AlphaHue,
                drawable=witness.Donor.AllowedToDraw,objects=negativeCounts.Objects,quads=negativeCounts.Quads,metadata});Save();
            Require(witness.Donor.AlphaHue==0&&negativeCounts.Objects==0&&negativeCounts.Quads==0,
                "frozen-time negative requires Alpha0/exact0objects0quads");
            using var negativeSurface=SpeedModeBindings.Current.Surface.GetImage();
            for(int step=1;step<=11;step++) {
                Time.Ticks+=(uint)(GUO.Game.Constants.ALPHA_TIME+1);
                using var image=await Capture("alpha-normal-step-"+step,false);
                var count=AdmissionCounts(witness.Donor);int expectedAlpha=Math.Min(255,step*25);
                _rows.Add(new{test="alpha-normal",step,tick=Time.Ticks,alpha=witness.Donor.AlphaHue,
                    expectedAlpha,objects=count.Objects,quads=count.Quads,group=count.Group});Save();
                Require(witness.Donor.AlphaHue==expectedAlpha,"normal alpha progression failed at step"+step);
                Require(count.Objects==1&&count.Quads==1,"normal admission requires exact1object1quad at step"+step);
            }
            var admitted=AdmissionCounts(witness.Donor);
            Require(witness.Donor.AlphaHue==255&&admitted.Objects==1&&admitted.Quads==1,
                "admitted donor requires Alpha255/exact1object1quad");
            uint frozenComparisonTick=Time.Ticks;
            using var before=await Capture("native-water-0080-reflections-off");
            var flags=await Surface("native-water-0080");
            using var nativeSurface=SpeedModeBindings.Current.Surface.GetImage();
            int footprint=ChangedNativeDonorFootprint(negativeSurface,nativeSurface,SpeedModeBindings.Current.Origin);
            _rows.Add(new{test="admitted-native-footprint",tick=Time.Ticks,changedPixels=footprint});Save();
            Require(footprint>0,"admitted donor lacks changed native surface footprint; stop, no retry");
            var donors=witness.ReadActualGeometry(nativeSurface,SpeedModeBindings.Current.Origin,1/_scene.Camera.Zoom,Time.Ticks/1000.0);
            _rows.Add(new{test="admitted-native-eligibility",donors.WaterPixels,donors.Eligible,donors.Controlled});Save();
            Require(donors.Controlled>0,"admitted footprint lacks unchanged eligible controlled donors; stop, no retry");
            _profile.SpeedModeReflections=true;await Warm();using var after=await Capture("native-water-0080-reflections-on");Bound("native-water-0080");
            Require(Time.Ticks==frozenComparisonTick,"reflection comparisons retain frozen admitted tick");
            var difference=ReflectionDiff(before,after,flags,"native-water-0080",false);
            int controlledChanged=NativeWaterWitness.ChangedControlled(before,after,donors.ControlledPixels);
            _rows.Add(new{test="controlled-native-water",metadata,donors.WaterPixels,donors.Eligible,donors.Controlled,
                changedWater=difference.water,controlledChanged,tick=Time.Ticks});Save();
            Require(controlledChanged>0&&difference.water>0,"eligible controlled donor lacks water pixels; stop, no retry");
            }
            Time.Ticks+=240000;await Warm();
            Record(SpeedModeWeather.Observe(world,_profile).Wetness==0,"retained wetness drains to dry");
            _profile.SpeedModeReflections=false;await Warm();using(var before=await Capture("dry-reflections-off")) {
                var flags=await Surface("dry");_profile.SpeedModeReflections=true;await Warm();
                using var after=await Capture("dry-reflections-on");var diff=ReflectionDiff(before,after,flags,"dry",true);
                Record(diff.floor==0,"dry floor never reflects");
            }

            _profile.LightingSpeedMode=false;await Warm();using(var before=await Capture("parent-off-before")) {
                Record(Runtime==null&&SpeedModeBindings.Current.Surface==null,"parent off clears publication and runtime");
                _profile.LightingSpeedMode=true;await Warm();using var on=await Capture("parent-on");
                _profile.LightingSpeedMode=false;await Warm();using var after=await Capture("parent-off-return");
                int changed=Diff(before,after);Record(changed==0,"parent-off exact native return");
                _rows.Add(new{test="parent-off-return",changedPixels=changed});
            }
            _profile.LightingSpeedMode=true;_profile.WaterStyle="uo";WaterRender.Changed(world);await Warm(12);
            using(var styled=await Capture("styled-water-fallback")) {
                Record(!OrderedWorldCapture.LastFrameOnPass&&WaterRender.IsStyled(_profile),
                    "existing styled-water rejection preserved");
                Record(Runtime==null||!Runtime.Bound,"styled water never binds speed weather publication");
                _rows.Add(new{test="styled-water-rejection",reason=OrderedWorldCapture.LastFallback,bound=Runtime?.Bound??false});
            }
            _profile.WaterStyle="classic";WaterRender.Changed(world);Packet(WeatherType.WT_RAIN,70);await Warm(12);Bound("classic-return");
            var oldTarget=_targets.WorldRenderTarget;var oldRuntime=Runtime;
            DisplayServer.WindowSetSize(new Vector2I(960,640));await Frame();
            _scene.Camera.Bounds.Width=960;_scene.Camera.Bounds.Height=640;_profile.GameWindowSize=new GUO.Compat.Point(960,640);
            await Warm();using(var resized=await Capture("resized-native-weather")) {
                Bound("resize");Record(oldTarget.IsDisposed&&!ReferenceEquals(oldTarget,_targets.WorldRenderTarget),"actual resize replaces target");
                Record(!GodotObject.IsInstanceValid(oldRuntime)&&Runtime!=null,"actual resize retires old weather owner");
                Record(resized.GetWidth()==960&&resized.GetHeight()==640,"actual resized frame dimensions");
                _rows.Add(new{test="resize",width=resized.GetWidth(),height=resized.GetHeight()});
            }
            var lastRuntime=Runtime;_game.SetScene(new LoginScene(world));await Frame();
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            Record(_scene.IsDestroyed&&Runtime==null&&!GodotObject.IsInstanceValid(lastRuntime),"actual unload retires weather runtime");
            Record(SpeedModeBindings.Current.Surface==null&&SpeedModeBindings.Current.LightTarget==null,"unload clears borrowed bindings");
            _rows.Add(new{test="unload-publication",surfaceRemaining=SpeedModeBindings.Current.Surface!=null,
                lightRemaining=SpeedModeBindings.Current.LightTarget!=null,weatherReleased=Read<bool>(lastRuntime,"_released"),
                weatherSurfaceCleared=Read<Texture2D>(lastRuntime,"_expectedSurface")==null,
                weatherColorCleared=Read<Texture2D>(lastRuntime,"_color")==null});
            Record(!NetClient.Socket.IsConnected,"stayed disconnected");_rows.Add(new{test="unload",destroyed=_scene.IsDestroyed,disconnected=!NetClient.Socket.IsConnected});
            Save();Require(_failures.Count==0,string.Join("; ",_failures));
            GD.Print("[native-water-witness] PASS actual-GameScene rain/fog/wet-paving/classic-water/rejection/off/resize/unload disconnected=1");
            GetTree().Quit(0);
        }
        catch(Exception error) { _failures.Add(error.ToString());Save();GD.PrintErr("[native-water-witness] FAIL "+error);GetTree().Quit(1); }
        finally { witness?.Dispose();if(_savedTick!=0) Time.Ticks=_savedTick; }
    }
}
