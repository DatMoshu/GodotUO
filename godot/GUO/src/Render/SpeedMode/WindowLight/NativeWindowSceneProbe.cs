// SPDX-License-Identifier: BSD-2-Clause
// Disconnected native-art GameScene proof. Never supplies receiver textures.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.Lights;
using GUO.Renderer.SpeedMode.Shadows;

namespace GUO.Renderer.SpeedMode.WindowLight;

public partial class NativeWindowSceneProbe : Node
{
    private string _out,_home,_stills;
    private readonly List<object> _rows=new();
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private).GetValue(owner);
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    private async Task Painted()=>await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
#if DEBUG
    private static FieldInfo SkipField=>typeof(WindowRayPassRd).GetField("ProbeSkip",BindingFlags.Static|BindingFlags.NonPublic)
        ??throw new InvalidOperationException("Native negative-control hook needs explicit lead authorization.");
    private static void Skip(bool value)=>SkipField.SetValue(null,value);
#endif

    public override async void _Ready()
    {
#if !DEBUG
        GD.PrintErr("[native-window-scene] Unsupported non-Debug probe; diagnostic control is Debug-only.");
        GetTree().Quit(1);
        return;
#else
        bool skipTouched=false;
        try {
            Check(!(bool)SkipField.GetValue(null),"diagnostic skip defaults off");
            _out=System.Environment.GetEnvironmentVariable("GUO_WINDOW_SCENE_OUT");
            Check(!string.IsNullOrWhiteSpace(_out)&&Path.IsPathRooted(_out),"Use an external evidence folder.");
            _out=Path.GetFullPath(_out);_home=Path.Combine(_out,"data","fixture");_stills=Path.Combine(_out,"stills");
            var args=OS.GetCmdlineUserArgs();int cache=Array.IndexOf(args,"--cache-dir");
            Check(cache>=0&&cache+1<args.Length&&Path.GetFullPath(args[cache+1])==Path.Combine(_home,"cache"),"Explicit isolated --cache-dir required.");
            Check(!args.Contains("--window-size"),"Use engine --resolution only; native resize proof must not pin the fixture window.");
            Check(!args.Any(a=>a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session"),"No accounts or shard sessions.");
            Check(!_out.StartsWith(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")),StringComparison.OrdinalIgnoreCase),"Evidence stays outside checkout.");
            Directory.CreateDirectory(_home);Directory.CreateDirectory(_stills);
            Settings.CustomSettingsFilepath=Path.Combine(_home,"settings.json");
            File.WriteAllText(Settings.CustomSettingsFilepath,"{}");
            GUO.Workspace.Workspace.RootOverride=Path.Combine(_home,"workspace");
            // Boot only after validating the isolated home; no packed child may boot first.
            AddChild(new GUO.Host.Main());
            for(int i=0;Client.Game?.Scene is not LoginScene;i++) { if(i>1200)throw new Exception("login timeout");await Frame(); }
            var game=Client.Game;var world=game.UO.World;
            Check(!GUO.Network.NetClient.Socket.IsConnected,"fixture socket is disconnected");
            var setup=typeof(GUO.Network.NetClient).GetMethod("SetupSocket",Private);
            setup.Invoke(GUO.Network.NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
            Settings.GlobalSettings.ProfilesPath=Path.Combine(_home,"profiles");
            typeof(ProfileManager).GetField("_rootPath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Settings.GlobalSettings.ProfilesPath);
            ProfileManager.Load("Offline","native-window-1037","native-window-1037");
            Check(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_home,StringComparison.OrdinalIgnoreCase),"fixture-only profile path");
            var profile=ProfileManager.CurrentProfile;
            profile.DefaultScale=1;profile.WindowBorderless=false;Settings.GlobalSettings.IsWindowMaximized=false;
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            profile.GameWindowFullSize=true;profile.TopbarGumpIsDisabled=true;
            profile.UseCircleOfTransparency=false;profile.NoColorObjectsOutOfRange=false;profile.DrawRoofs=false;
            profile.LightingSpeedMode=true;profile.SpeedModeWindowLight=true;profile.SpeedModeSunShadows=true;
            profile.SpeedModeLampShadows=0;profile.SpeedModeSoften=0;
            profile.UseCustomLightLevel=true;profile.LightLevel=0;profile.LightLevelType=0;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;world.Player.Name="Disconnected native window fixture";

            // Metadata is unchanged. Faces and art dimensions come from installed native art.
            var catalog=new List<object>();ushort windowId=0;WallFaces.Face face=default;
            foreach(var pair in WindowLightTable.BuiltIn.OrderBy(p=>p.Key)) {
                var faces=WallFaces.For(pair.Key);var td=game.UO.FileManager.TileData.StaticData[pair.Key];
                catalog.Add(new{graphic=pair.Key,shape=pair.Value.Shape.ToString(),faces=faces.Count,td.Name,flags=(ulong)td.Flags});
                if(windowId==0&&pair.Value.Shape!=OpeningShape.None&&faces.Count>0) { windowId=pair.Key;face=faces.First; }
            }
            File.WriteAllText(Path.Combine(_out,"data","window-catalog.json"),JsonSerializer.Serialize(catalog,new JsonSerializerOptions{WriteIndented=true}));
            Check(windowId!=0,"existing metadata has a native wall-face window");
            ushort Native(Func<ushort,bool> accept) {
                for(int id=1;id<Math.Min(0x4000,game.UO.FileManager.TileData.StaticData.Length);id++) {
                    ushort graphic=(ushort)id;if(!accept(graphic))continue;
                    ref readonly var art=ref game.UO.Arts.GetArt(graphic);
                    if(art.Texture!=null&&art.UV.Width>0&&art.UV.Height>0)return graphic;
                }
                throw new InvalidOperationException("Native floor/roof art unavailable.");
            }
            var data=game.UO.FileManager.TileData.StaticData;
            ushort floor=Native(g=>data[g].IsSurface&&data[g].Height<4&&!data[g].IsRoof&&!data[g].IsBridge&&data[g].Name?.Contains("floor",StringComparison.OrdinalIgnoreCase)==true);
            ushort roof=Native(g=>data[g].IsRoof);
            // A small deterministic in-memory room made of native Static art. Original map data is never written or removed.
            int wx=1420,wy=1730;world.Map.GetMapZ(wx,wy,out sbyte ground,out _);
            int inner=(int)MathF.Floor((face.AlongX?wy:wx)+face.Offset+.6f),index=1000000;
            void StaticAt(ushort graphic,int x,int y,int z) {
                var s=Static.Create(world,graphic,0,index++);s.SetInWorldTile((ushort)x,(ushort)y,(sbyte)z);
            }
            for(int across=-4;across<=4;across++)for(int depth=0;depth<5;depth++) {
                int x=face.AlongX?wx+across:inner+depth,y=face.AlongX?inner+depth:wy+across;
                StaticAt(floor,x,y,ground+1);StaticAt(roof,x,y,ground+24);
            }
            StaticAt(windowId,wx,wy,ground);
            int px=face.AlongX?wx-3:inner+3,py=face.AlongX?inner+3:wy-3;
            world.Player.SetInWorldTile((ushort)px,(ushort)py,(sbyte)(ground+1));
            File.WriteAllText(Path.Combine(_out,"data","room.json"),JsonSerializer.Serialize(new{windowId,floor,roof,wx,wy,ground,face.AlongX,face.Offset,px,py,nativeStaticCount=index-1000000,profilePath=ProfileManager.ProfilePath},new JsonSerializerOptions{WriteIndented=true}));
            world.ShardClock.Set(13,0,0,Time.Ticks);
            var scene=new GameScene(world);game.SetScene(scene);game.SetWindowSize(1280,720);
            for(int i=0;i<12;i++)await Frame();game.SetProcess(false);
            var draw=game.GetType().GetMethod("DrawFrame",Private);
            var batcher=Field<UltimaBatcher2D>(game,"_uoSpriteBatch");
            OrderedWorldDevice Device()=>Field<OrderedWorldDevice>(batcher.OrderedWorldProbe,"_device");
            async Task Draw(string name) {
                draw.Invoke(game,null);await Painted();
                using var image=GetViewport().GetTexture().GetImage();image.SavePng(Path.Combine(_stills,name+".png"));
            }
            async Task<byte[]> Light() {
                byte[] result=null;Exception failure=null;
                RenderingServer.CallOnRenderThread(Callable.From(()=>{try { var d=Device();Check(d.SpeedLightReady,"native core light target ready");result=d.ReadSpeedLightRT(); }catch(Exception e){failure=e;}}));
                await Painted();if(failure!=null)throw failure;return result;
            }
            int Positive(byte[] before,byte[] after) {
                Check(before.Length==after.Length,"same target layout");int n=0;
                for(int at=0;at<before.Length;at+=8) {
                    float a=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(before,at));
                    float b=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(after,at));
                    if(float.IsFinite(a)&&float.IsFinite(b)&&b>a+.00005f)n++;
                }
                return n;
            }
            _=SkipField;skipTouched=true;
            async Task<int> Case(string name,int hour,bool requireOffers=true) {
                world.ShardClock.Set((byte)hour,0,0,Time.Ticks);Skip(true);await Draw(name+"-control-warm");await Draw(name+"-control");var before=await Light();
                await Draw(name+"-control-repeat");var repeat=await Light();Check(before.AsSpan().SequenceEqual(repeat),"native light control stable before shaft comparison");
                Skip(false);await Draw(name+"-shaft-warm");await Draw(name+"-shaft");var after=await Light();
                var inputs=Field<SpeedShadowInputs>(scene,"_speedShadows");var frame=Field<SpeedLightFrame>(scene,"_speedLights");
                int positive=Positive(before,after);
                var pass=Field<WindowRayPassRd>(inputs,"_rays");
                _rows.Add(new{name,hour,windows=inputs.Windows.Count,rays=inputs.Rays.Count,sunStamps=inputs.Sun.Count,
                    currentSunMask=frame.SunMask.IsValid,positivePixels=positive,resources=pass?.ResourceCount??0,buffer=pass?.BufferBytes??0,
                    zoom=scene.Camera.Zoom,viewTransform=scene.Camera.ViewTransform.ToString(),
                    origin=frame.Origin.ToString(),target=Device().Size.ToString(),nativeObjects=scene.RenderedObjectsCount});
                File.WriteAllText(Path.Combine(_out,"data","native-results.json"),JsonSerializer.Serialize(_rows,new JsonSerializerOptions{WriteIndented=true}));
                Check(frame.SunMask.IsValid,"current sun mask exercised");
                if(requireOffers)Check(inputs.Windows.Count>0&&inputs.Rays.Count>0,"native offers exercised");
                if(inputs.Rays.Count==0)Check(positive==0&&(pass?.ResourceCount??0)==0,"camera rejection has no stale shaft output/resources");
                return positive;
            }
            int first=await Case("native-13h",13),second=await Case("native-15h",15);
            scene.Camera.Zoom=2.5f;scene.Camera.Update(true,0,GUO.Compat.Point.Zero);await Case("camera-2-5",13,false);
            Check(scene.Camera.Zoom==2.5f,"actual camera zoom is 2.5");
            scene.Camera.Zoom=1;scene.Camera.Update(true,0,GUO.Compat.Point.Zero);
            game.SetWindowSize(960,640);for(int i=0;i<5;i++)await Frame();await Case("resize-960-640",13);
            Check(Device().Size==new Vector2I(960,640),"native target follows fixture window resize");
            profile.LightingSpeedMode=false;await Draw("off-warm");await Draw("off-before");
            using var beforeOff=GetViewport().GetTexture().GetImage();var off=beforeOff.GetData();
            profile.LightingSpeedMode=true;await Draw("on-again");
            profile.LightingSpeedMode=false;await Draw("off-return-warm");await Draw("off-return");
            using var afterOff=GetViewport().GetTexture().GetImage();Check(off.AsSpan().SequenceEqual(afterOff.GetData()),"native frozen off bytes exact");
            Check(SpeedModeBindings.Current.LightTarget==null&&SpeedModeBindings.Current.Surface==null,"off clears native bindings");
            var retainedInputs=Field<SpeedShadowInputs>(scene,"_speedShadows");
            game.SetScene(new LoginScene(world));await Painted();await Painted();
            Check(Field<WindowRayPassRd>(retainedInputs,"_rays")==null,"unload retires shaft pass");
            Check(!GUO.Network.NetClient.Socket.IsConnected,"no shard connection");
            bool visible=first>0||second>0;
            File.WriteAllText(Path.Combine(_out,"data","acceptance.json"),JsonSerializer.Serialize(new{pass=visible,offDiff=0,bindingsClear=true,unloadReleased=true,disconnected=true,first,second,limitation=visible?null:"Native visible geometry conservatively rejected shaft output; no guard bypass applied."},new JsonSerializerOptions{WriteIndented=true}));
            if(!visible) { GD.PrintErr("[native-window-scene] BLOCKED native offers/mask present but receiver/path geometry rejects shafts; no forced bypass.");GetTree().Quit(2); }
            else { GD.Print("[native-window-scene] PASS native-game-scene windows/mask/nonzero-shafts camera/resize off-diff=0 unload=clear disconnected=1");GetTree().Quit(0); }
        }
        catch(Exception e) { GD.PrintErr("[native-window-scene] FAIL "+e);GetTree().Quit(1); }
        finally { if(skipTouched) { Skip(false);Check(!(bool)SkipField.GetValue(null),"diagnostic skip reset off"); } }
#endif
    }
}
