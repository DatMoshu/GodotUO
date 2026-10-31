// SPDX-License-Identifier: BSD-2-Clause
// Task1077: real native cover/source/surface/rain attribution; no authored receiver buffers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
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

public partial class NativeRoofWeatherProbe : Node
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
        _root=System.Environment.GetEnvironmentVariable("GUO_ROOF_WEATHER_OUT");
        if(string.IsNullOrWhiteSpace(_root)) throw new InvalidOperationException("Set GUO_ROOF_WEATHER_OUT to an external evidence run.");
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
    private void Save()=>File.WriteAllText(Path.Combine(_root,"data","native-roof-weather-results.json"),
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
    private GUO.Game.GameObjects.Static _floor;
    private int _px=1417,_py=1734,_z;
    private async Task CoverCase(string name,bool expectedCover)
    {
        var source=SurfaceRow.For(_floor);
        Record(((source.Data&16)!=0)==expectedCover,"physical source cover "+name);
        var rain=Read<SpeedModeRainPass>(Runtime,"_rain");
        var material=(ShaderMaterial)rain.Material;
        int protectedSamples=0,exteriorSamples=0,protectedChanged=0,exteriorChanged=0,unknownChanged=0;
        uint start=Time.Ticks;
        for(int phase=0;phase<6;phase++) {
            Time.Ticks=start+(uint)(phase*137);
            material.SetShaderParameter("component_mask",0);
            using var before=await Capture(name+"-"+phase+"-rain-skipped");Bound(name);
            using var surface=SpeedModeBindings.Current.Surface.GetImage();
            var origin=SpeedModeBindings.Current.Origin;
            material.SetShaderParameter("component_mask",7);
            using var after=await Capture(name+"-"+phase+"-rain");Bound(name);
            var a=before.GetData();var b=after.GetData();
            int inside=0,outside=0,insideWet=0,outsideWet=0,unknownWet=0,insideCovered=0;
            for(int y=0;y<surface.GetHeight();y++)for(int x=0;x<surface.GetWidth();x++) {
                int i=(y*surface.GetWidth()+x)*4;
                bool changed=a[i]!=b[i]||a[i+1]!=b[i+1]||a[i+2]!=b[i+2]||a[i+3]!=b[i+3];
                var p=surface.GetPixel(x,y);int packed=(int)MathF.Round(p.A),kind=packed&15;
                if(kind==0&&changed)unknownWet++;
                if(kind!=1||Math.Abs(p.B-_z)>.1f)continue;
                float wx=p.R+origin.X,wy=p.G+origin.Y;
                bool inner=wx>_px+.15f&&wx<_px+.85f&&wy>_py+.15f&&wy<_py+.85f;
                bool outer=wx>_px+2.15f&&wx<_px+2.85f&&wy>_py+.15f&&wy<_py+.85f;
                if(inner){inside++;if((packed&16)!=0)insideCovered++;if(changed)insideWet++;}
                if(outer){outside++;if(changed)outsideWet++;}
            }
            protectedSamples+=inside;exteriorSamples+=outside;
            protectedChanged+=insideWet;exteriorChanged+=outsideWet;unknownChanged+=unknownWet;
            _rows.Add(new{test="native-cover",name,phase,tick=Time.Ticks,sourceData=source.Data,
                expectedCover,inside,outside,insideCovered,insideWet,outsideWet,unknownWet,
                sequence=SpeedModeBindings.Current.Sequence});Save();
            Record(inside>0&&outside>0,"actual native receiver masks present "+name+phase);
            Record(expectedCover?insideCovered==inside:insideCovered==0,"published cover matches physical source "+name+phase);
        }
        Record(exteriorChanged>0,"known exterior rain remains active "+name);
        Record(expectedCover?protectedChanged==0:protectedChanged>0,"native protected/exposed rain "+name);
        Record(unknownChanged==0,"unknown native pixels reject rain "+name+": "+unknownChanged);
        material.SetShaderParameter("component_mask",7);Save();
    }
    public override async void _Ready()
    {
#if !DEBUG
        GD.PrintErr("[native-roof-weather] Debug fixture only");GetTree().Quit(1);return;
#else
        var roofs=new List<GUO.Game.GameObjects.Static>();
        try {
            Require(!OS.GetCmdlineUserArgs().Any(a=>a is "--account" or "--password" or "--auto-login" or "--autologin"),"disconnected fixture has no credentials");
            for(int i=0;Client.Game?.Scene is not LoginScene;i++){if(i>1200)throw new Exception("login timeout");await Frame();}
            _game=Client.Game;var world=_game.UO.World;Require(!NetClient.Socket.IsConnected,"disconnected bootstrap");
            var setup=typeof(NetClient).GetMethod("SetupSocket",Private);
            setup.Invoke(NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
            Settings.GlobalSettings.ProfilesPath=Path.Combine(_root,"data/fixture/profiles");
            ProfileManager.Load("Offline","roof-weather1077","roof-weather1077");_profile=ProfileManager.CurrentProfile;
            Require(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_root,StringComparison.OrdinalIgnoreCase),"isolated profile");
            _profile.DefaultScale=1;_profile.GameWindowFullSize=true;_profile.TopbarGumpIsDisabled=true;
            _profile.UseCircleOfTransparency=false;_profile.NoColorObjectsOutOfRange=false;_profile.EnableDeathScreen=false;
            _profile.LightingSpeedMode=true;_profile.SpeedModeSunShadows=false;_profile.SpeedModeLampShadows=0;
            _profile.SpeedModeWindowLight=false;_profile.SpeedModeWeather=true;_profile.SpeedModeFog=false;
            _profile.SpeedModeMorningHaze=0;_profile.SpeedModeReflections=false;_profile.WaterStyle="classic";_profile.WindStrength=0;
            _profile.DrawRoofs=false;_profile.ShadowsEnabled=false;_profile.ShadowsStatics=false;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;world.Player.SetInWorldTile((ushort)_px,(ushort)_py,20);
            _scene=new GameScene(world);_game.SetScene(_scene);_game.SetWindowSize(1280,720);_scene.Camera.Zoom=1;
            for(int i=0;i<12;i++)await Frame();
            world.Map.GetMapZ(_px,_py,out sbyte ground,out _);_z=ground+1;
            world.Player.SetInWorldTile((ushort)_px,(ushort)_py,(sbyte)_z);
            int index=1100000;
            GUO.Game.GameObjects.Static Place(ushort graphic,int x,int y,int z){
                var o=GUO.Game.GameObjects.Static.Create(world,graphic,0,index++);o.SetInWorldTile((ushort)x,(ushort)y,(sbyte)z);return o;
            }
            // Source-decoded full native diamonds: floor0495 and roof05E4, no substituted textures.
            Require(_game.UO.FileManager.TileData.StaticData[0x0495].IsSurface
                &&_game.UO.FileManager.TileData.StaticData[0x05E4].IsRoof,"native metadata floor/roof");
            for(int y=-4;y<=4;y++)for(int x=-4;x<=4;x++){
                var floor=Place(0x0495,_px+x,_py+y,_z);if(x==0&&y==0)_floor=floor;
            }
            _targets=Read<RenderTargets>(_game,"_renderTargets");_draw=_game.GetType().GetMethod("DrawFrame",Private);
            _scene.UpdateDrawPosition=true;for(int i=0;i<12;i++)await Frame();_game.SetProcess(false);
            _savedTick=Time.Ticks;Packet(WeatherType.WT_RAIN,70);await Warm();
            await CoverCase("no-roof",false);
            for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++)roofs.Add(Place(0x05E4,_px+x,_py+y,_z+20));
            _scene.UpdateDrawPosition=true;_game.SetProcess(true);for(int i=0;i<12;i++)await Frame();_game.SetProcess(false);await Warm();
            await CoverCase("physical-roof-cutaway",true);
            foreach(var roof in roofs)roof.Destroy();roofs.Clear();
            _scene.UpdateDrawPosition=true;_game.SetProcess(true);for(int i=0;i<12;i++)await Frame();_game.SetProcess(false);await Warm();
            await CoverCase("roof-removed",false);
            Require(!NetClient.Socket.IsConnected,"remains disconnected");Save();
            if(_failures.Count>0)throw new InvalidOperationException(string.Join("; ",_failures));
            GD.Print("[native-roof-weather] PASS source/surface/known-exposed/protected/unknown");GetTree().Quit(0);
        }catch(Exception error){_failures.Add(error.ToString());Save();GD.PrintErr("[native-roof-weather] FAIL "+error);GetTree().Quit(1);}
        finally{foreach(var roof in roofs)roof.Destroy();}
#endif
    }
}
