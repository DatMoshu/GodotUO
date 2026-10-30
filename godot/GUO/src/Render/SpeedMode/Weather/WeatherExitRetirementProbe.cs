// SPDX-License-Identifier: BSD-2-Clause
// Task992 chief21848: regression for actual child-exit-before-scene-retirement order.
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
namespace GUO.Renderer.SpeedMode.Weather;
public partial class WeatherExitRetirementProbe : Node
{
    private string _root;private GUO.GameController _game;private MethodInfo _draw;
    private readonly List<object> _rows=new();private readonly List<string> _failures=new();
    private static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Read<T>(object obj,string name)=>(T)obj.GetType().GetField(name,Fields).GetValue(obj);
    private void Check(bool ok,string name){if(!ok)_failures.Add(name);}
    private void Save()=>File.WriteAllText(Path.Combine(_root,"results.json"),JsonSerializer.Serialize(new{rows=_rows,failures=_failures},new JsonSerializerOptions{WriteIndented=true}));
    public override void _EnterTree(){
        _root=System.Environment.GetEnvironmentVariable("GUO_WEATHER_EXIT_OUT")??throw new Exception("GUO_WEATHER_EXIT_OUT required");Directory.CreateDirectory(_root);
        string home=Path.Combine(_root,"fixture");Directory.CreateDirectory(home);
        GUO.Workspace.Workspace.RootOverride=Path.Combine(home,"workspace");System.Environment.SetEnvironmentVariable("UO_WORKSPACE_DIR",GUO.Workspace.Workspace.RootOverride);
        System.Environment.SetEnvironmentVariable("UO_CACHE_DIR",Path.Combine(home,"cache"));Settings.CustomSettingsFilepath=Path.Combine(home,"settings.json");
        File.WriteAllText(Settings.CustomSettingsFilepath,JsonSerializer.Serialize(new{autologin=false,saveaccount=false,login_music=false,profilespath=Path.Combine(home,"profiles"),plugins=Array.Empty<string>()}));
    }
    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    private async Task Draw(int n=6){for(int i=0;i<n;i++){_draw.Invoke(_game,null);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);}}
    private GameScene Enter(World world){
        ProfileManager.Load("Offline","exit-fixture","exit-fixture");var p=ProfileManager.CurrentProfile;
        p.GameWindowFullSize=true;p.GameWindowSize=new GUO.Compat.Point(1280,720);p.TopbarGumpIsDisabled=true;p.UseCircleOfTransparency=false;
        p.LightingSpeedMode=true;p.SpeedModeLampShadows=0;p.SpeedModeSunShadows=false;p.SpeedModeWindowLight=false;
        p.SpeedModeWeather=false;p.SpeedModeFog=false;p.SpeedModeReflections=false;p.WaterStyle="classic";
        world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;world.Player.SetInWorldTile(1431,1691,20);
        var scene=new GameScene(world);_game.SetScene(scene);return scene;
    }
    public override async void _Ready(){try{
        if(DisplayServer.GetName()=="headless")throw new Exception("Real renderer required");
        for(int i=0;Client.Game?.Scene is not LoginScene;i++){if(i>1200)throw new Exception("bootstrap timeout");await Frame();}
        _game=Client.Game;var world=_game.UO.World;if(NetClient.Socket.IsConnected)throw new Exception("must stay disconnected");
        var setup=typeof(NetClient).GetMethod("SetupSocket",Fields);setup.Invoke(NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
        Settings.GlobalSettings.ProfilesPath=Path.Combine(_root,"fixture","profiles");
        _draw=_game.GetType().GetMethod("DrawFrame",Fields);var batcher=Read<UltimaBatcher2D>(_game,"_uoSpriteBatch");
        for(int cycle=0;cycle<2;cycle++){
            var scene=Enter(world);for(int i=0;i<12;i++)await Frame();_game.SetProcess(false);await Draw();
            var capture=batcher.OrderedWorldProbe;var consumer=Read<SpeedModeWeatherRuntime>(scene,"_speedWeatherRuntime");
            if(consumer==null||!GodotObject.IsInstanceValid(consumer))throw new Exception("actual weather consumer absent");
            var items=Read<Rid[]>(consumer,"_items");var before=Array.ConvertAll(items,x=>x.ToString());
            Check(Array.TrueForAll(items,x=>x.IsValid),"three live native items before exit");
            Check(SpeedModeBindings.Current.Surface!=null&&SpeedModeBindings.Current.LightTarget!=null,"new scene produces before exit");
            // A real pending DrawFrame in cycle1; no capture or publication is injected.
            if(cycle==1)_draw.Invoke(_game,null);
            consumer.GetParent().RemoveChild(consumer); // _ExitTree frees owned CanvasItems; native Node remains alive until Scene.Unload frees it.
            bool sameManaged=ReferenceEquals(Read<SpeedModeWeatherRuntime>(scene,"_speedWeatherRuntime"),consumer);
            bool released=Read<bool>(consumer,"_released"),nativeAlive=GodotObject.IsInstanceValid(consumer);
            // Same core callback reached by GameController.ExitTree -> Scene.Dispose -> RetireScene.
            _game.SetScene(new LoginScene(world));
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);await Frame();
            int stale=Array.FindAll(items,x=>x.IsValid).Length;
            var device=Read<OrderedWorldDevice>(capture,"_device");
            _rows.Add(new{cycle,queued=cycle==1,sameManagedConsumerBeforeRetire=sameManaged,released,nativeAlive,beforeItems=before,
                afterItems=Array.ConvertAll(items,x=>x.ToString()),staleOwnedHandles=stale,
                surfaceRemaining=SpeedModeBindings.Current.Surface!=null,lightRemaining=SpeedModeBindings.Current.LightTarget!=null,
                surfaceReady=device.SurfaceReady,lightReady=device.SpeedLightReady,canBegin=capture.CanBegin});Save();
            Check(sameManaged&&released&&nativeAlive&&!GodotObject.IsInstanceValid(consumer),"exact child-first live Node/freed owned-items identity then scene disposal");
            Check(stale==0,"released consumer must not retain freed nonzero CanvasItem handles cycle"+cycle);
            Check(SpeedModeBindings.Current.Surface==null&&SpeedModeBindings.Current.LightTarget==null,"no old ticket republish cycle"+cycle);
            Check(!device.SurfaceReady&&!device.SpeedLightReady&&capture.CanBegin,"optional producer retired/new scene allowed cycle"+cycle);
        }
        Check(!NetClient.Socket.IsConnected,"stayed disconnected");Save();
        if(_failures.Count!=0)throw new Exception(string.Join("; ",_failures));
        GD.Print("[weather-exit-regression] PASS child-first=2 actual-freed-identity=2 stale-handles=0 queued-ticket=retired new-scene=reused disconnected=1");GetTree().Quit(0);
    }catch(Exception e){_failures.Add(e.ToString());Save();GD.PrintErr("[weather-exit-regression] FAIL "+e);GetTree().Quit(1);}}
}
