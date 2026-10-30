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

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class NativeLightAttributionProbe : Node
{
    private string _out,_home,_stills;
    private readonly List<object> _rows=new();
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,Private).GetValue(owner);
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    private async Task Painted()=>await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
    public override async void _Ready()
    {
#if !DEBUG
        GD.PrintErr("[native-light-attribution-scene] Unsupported non-Debug probe; native fixture is Debug-only.");
        GetTree().Quit(1);
        return;
#else
        try {
            _out=System.Environment.GetEnvironmentVariable("GUO_LIGHT_ATTRIBUTION_OUT");
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
            ProfileManager.Load("Offline","native-light-attribution-1054","native-light-attribution-1054");
            Check(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_home,StringComparison.OrdinalIgnoreCase),"fixture-only profile path");
            var profile=ProfileManager.CurrentProfile;
            File.WriteAllText(Path.Combine(_out,"data","original-fixture-profile.json"),JsonSerializer.Serialize(new{
                profile.ShadowsEnabled,profile.ShadowsStatics,
                provenance="Fresh isolated fixture profile before controls; not the owner recording profile."}));
            profile.DefaultScale=1;profile.WindowBorderless=false;Settings.GlobalSettings.IsWindowMaximized=false;
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            profile.GameWindowFullSize=true;profile.TopbarGumpIsDisabled=true;
            profile.UseCircleOfTransparency=false;profile.NoColorObjectsOutOfRange=false;profile.DrawRoofs=false;
            profile.LightingSpeedMode=true;profile.SpeedModeWindowLight=false;profile.SpeedModeSunShadows=false;
            profile.SpeedModeLampShadows=0;profile.SpeedModeSoften=0;
            profile.UseCustomLightLevel=true;profile.LightLevel=24;profile.LightLevelType=0;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;world.Player.Name="Disconnected light attribution fixture";


            var mobile=world.Player;mobile.Direction=Direction.South;
            ushort Native(Func<ushort,bool> predicate) {
                for(ushort id=1;id<0x4000;id++) {
                    if(!predicate(id))continue;
                    ref readonly var art=ref game.UO.Arts.GetArt(id);
                    if(art.Texture!=null&&art.UV.Width>0&&art.UV.Height>0)return id;
                }
                throw new InvalidOperationException("Installed native fixture art witness missing.");
            }
            ushort lamp=0x0B1D;
            if(!game.UO.FileManager.TileData.StaticData[lamp].IsLight)
                lamp=Native(id=>game.UO.FileManager.TileData.StaticData[id].IsLight);
            ushort floor=Native(id=>{var td=game.UO.FileManager.TileData.StaticData[id];return td.IsSurface&&!td.IsRoof&&!td.IsBridge&&td.Height<4&&td.Name?.Contains("floor",StringComparison.OrdinalIgnoreCase)==true;});
            int px=1417,py=1734;
            mobile.SetInWorldTile((ushort)px,(ushort)py,20);
            var scene=new GameScene(world);game.SetScene(scene);game.SetWindowSize(1280,720);
            // Native chunk loading must finish before sampling floor height/placing scratch art.
            for(int i=0;i<12;i++)await Frame();
            world.Map.GetMapZ(px,py,out sbyte ground,out _);int z=ground+1;
            mobile.SetInWorldTile((ushort)px,(ushort)py,(sbyte)z);
            int index=1000000;
            void Place(ushort graphic,int x,int y,int height) {
                var item=Static.Create(world,graphic,0,index++);item.SetInWorldTile((ushort)x,(ushort)y,(sbyte)height);
            }
            for(int y=-4;y<=4;y++)for(int x=-4;x<=4;x++)Place(floor,px+x,py+y,z);
            int lx=px-2,ly=py;Place(lamp,lx,ly,z);
            world.ShardClock.Set(12,0,0,Time.Ticks);
            File.WriteAllText(Path.Combine(_out,"data","fixture.json"),JsonSerializer.Serialize(new{lamp,floor,px,py,z,lx,ly,staticCount=index-1000000,profilePath=ProfileManager.ProfilePath},new JsonSerializerOptions{WriteIndented=true}));
            scene.UpdateDrawPosition=true;
            for(int i=0;i<12;i++)await Frame();game.SetProcess(false);
            var draw=game.GetType().GetMethod("DrawFrame",Private);
            var batcher=Field<UltimaBatcher2D>(game,"_uoSpriteBatch");
            OrderedWorldDevice Device()=>Field<OrderedWorldDevice>(batcher.OrderedWorldProbe,"_device");
            async Task<byte[]> Draw(string name) {
                draw.Invoke(game,null);await Painted();
                Check(OrderedWorldCapture.LastFrameOnPass,"native RD frame completed");
                using var image=GetViewport().GetTexture().GetImage();image.SavePng(Path.Combine(_stills,name+".png"));return image.GetData();
            }
            async Task<(byte[] Light,string Trace,byte[] Surface)> Sample(string name) {
                string trace=scene.CaptureSpeedLightDiagnostics(batcher);
                var device=Device();bool lightReady=device.SpeedLightReady;
                byte[] pixels=null,surface=null;Exception failure=null;
                RenderingServer.CallOnRenderThread(Callable.From(()=>{try {
                    if(lightReady) pixels=device.ReadSpeedLightRT();
                    if(device.SurfaceReady)surface=device.ReadSurfaceRT();
                }catch(Exception error){failure=error;}}));
                await Painted();if(failure!=null)throw failure;
                Check(trace!=null,"completed attribution callback");
                File.WriteAllText(Path.Combine(_out,"data",name+".json"),trace);
                return(pixels,trace,surface);
            }
            string Sources(string trace) {
                using var json=JsonDocument.Parse(trace);
                return JsonSerializer.Serialize(json.RootElement.GetProperty("offers").EnumerateArray().Select(e=>new {
                    x=e.GetProperty("X").GetInt32(),y=e.GetProperty("Y").GetInt32(),z=e.GetProperty("Z").GetInt32(),
                    radius=e.GetProperty("Radius").GetSingle(),energy=e.GetProperty("Energy").GetSingle(),
                    dx=e.GetProperty("OffsetX").GetSingle(),dy=e.GetProperty("OffsetY").GetSingle(),
                    colour=e.GetProperty("colour").GetRawText(),curve=e.GetProperty("curve").GetString(),
                    flicker=e.GetProperty("Flicker").GetSingle()
                }));
            }
            string Selected(string trace) {
                using var json=JsonDocument.Parse(trace);
                return JsonSerializer.Serialize(json.RootElement.GetProperty("offers").EnumerateArray()
                    .Where(e=>e.GetProperty("row").GetInt32()>=0).Select(e=>new{
                        offer=e.GetProperty("offer").GetInt32(),row=e.GetProperty("row").GetInt32(),
                        position=e.GetProperty("shadowPosition").GetRawText()}));
            }
            string baselineSelection=null;
            string prefix="";
            byte[] noShadowLight=null,noShadowScreen=null,noShadowSurface=null,fallbackScreen=null;
            string baselineSources=null;
            int Darkened(byte[] before,byte[] after) {
                Check(before.Length==after.Length,"same light target dimensions");int count=0;
                for(int at=0;at<before.Length;at+=8) {
                    bool lower=false;
                    for(int c=0;c<3;c++) {
                        float a=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(before,at+c*2));
                        float b=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(after,at+c*2));
                        Check(float.IsFinite(a)&&float.IsFinite(b),"finite light pixels");
                        if(a>b+.00005f)lower=true;
                    }
                    if(lower)count++;
                }
                return count;
            }
            async Task Case(string name,bool classic,bool sun,int lamps,bool inject=false) {
                string label=prefix+name;
                profile.ShadowsEnabled=classic;profile.ShadowsStatics=classic;
                profile.SpeedModeSunShadows=sun;profile.SpeedModeLampShadows=lamps;
                await Draw(label+"-warm");
                OrderedWorldCapture.InjectSpeedLightFailure=inject;
                byte[] screen;
                try { screen=await Draw(label); } finally { OrderedWorldCapture.InjectSpeedLightFailure=false; }
                var sample=await Sample(label);
                using var json=JsonDocument.Parse(sample.Trace);var root=json.RootElement;
                int classicCount=root.GetProperty("capturedClassicMode8Occurrences").GetInt32();
                Check(classic?classicCount>0:classicCount==0,"actual captured classic mode8 gate "+name);
                if(inject) {
                    Check(!root.GetProperty("lightPublished").GetBoolean(),"failed replacement is not published");
                    Check(classicCount>0,"classic fallback retained on failed light transaction");
                    fallbackScreen=screen;
                    _rows.Add(new{name=label,classicCount,fallback=true});return;
                }
                Check(root.GetProperty("lightPublished").GetBoolean()&&root.GetProperty("surfacePublished").GetBoolean(),"current published surface/light "+name);
                Check(sample.Light!=null,"actual light target "+name);
                Check(root.GetProperty("origin").GetRawText()==root.GetProperty("frameOrigin").GetRawText(),"frame/binding origin agree");
                Check(root.GetProperty("frameTarget")[0].GetInt32()==Device().Size.X
                    &&root.GetProperty("frameTarget")[1].GetInt32()==Device().Size.Y,"current target dimensions");
                Check(root.GetProperty("sunMask").GetBoolean()==sun,"sun mask isolation "+name);
                var offers=root.GetProperty("offers").EnumerateArray().ToArray();
                int rows=offers.Count(e=>e.GetProperty("row").GetInt32()>=0);
                Check(rows==lamps,"selected row budget isolation "+name);
                if(lamps==1) Check(offers.Any(e=>e.GetProperty("X").GetInt32()==lx&&e.GetProperty("Y").GetInt32()==ly
                    &&e.GetProperty("row").GetInt32()==0&&e.GetProperty("mobileMask").GetBoolean()
                    &&e.GetProperty("stamps").GetInt32()>0),"fixture lamp selected with native mobile stamps");
                if(name=="one-lamp-only")Check(!root.GetProperty("profile").GetProperty("SpeedModeSunShadows").GetBoolean(),"lamp positive requires SUN OFF");
                if(lamps>0) {
                    baselineSelection??=Selected(sample.Trace);
                    Check(Selected(sample.Trace)==baselineSelection,"same actual selected source IDs and rows");
                }
                if(name=="no-shadow"){noShadowLight=sample.Light;noShadowScreen=screen;noShadowSurface=sample.Surface;}
                if(name=="classic-only")Check(!noShadowScreen.AsSpan().SequenceEqual(screen),"classic changes actual composed screen");
                int darker=Darkened(noShadowLight,sample.Light);
                if(sun||lamps>0)Check(darker>0,"attributable light output darkening "+name);
                // Classic mode8 participates in the existing surface pass. Record its
                // output delta; equality across different receiver geometry is not a valid gate.
                int surfaceChangedBytes=noShadowSurface.Zip(sample.Surface,(a,b)=>a==b?0:1).Sum();
                int lightChangedBytes=noShadowLight.Zip(sample.Light,(a,b)=>a==b?0:1).Sum();
                if(!classic)Check(surfaceChangedBytes==0,"optional shadow controls retain actual receiver surface");
                string sources=Sources(sample.Trace);
                if(baselineSources==null)baselineSources=sources;
                Check(sources==baselineSources,"all actual offers/energy/flicker stable across controls");
                await Draw(label+"-repeat");var repeat=await Sample(label+"-repeat");
                Check(Sources(repeat.Trace)==sources,"repeat actual sources stable");
                Check(Selected(repeat.Trace)==Selected(sample.Trace),"repeat selected source IDs and rows stable");
                Check(repeat.Light!=null&&sample.Light.AsSpan().SequenceEqual(repeat.Light),"repeat actual light bytes stable");
                Check(sample.Surface.AsSpan().SequenceEqual(repeat.Surface),"repeat actual receiver bytes stable");
                using var repeated=JsonDocument.Parse(repeat.Trace);
                Check(repeated.RootElement.GetProperty("Sequence").GetInt64()>root.GetProperty("Sequence").GetInt64(),"fresh published surface sequence");
                _rows.Add(new{name=label,classicCount,rows,sun,darkenedLightPixels=darker,lightChangedBytes,surfaceChangedBytes,sourceStable=true,repeatedLightBytesEqual=true});
                File.WriteAllText(Path.Combine(_out,"data","native-results.json"),JsonSerializer.Serialize(_rows,new JsonSerializerOptions{WriteIndented=true}));
            }
            // All sources stay enabled; only their shadow passes change.
            await Case("no-shadow",false,false,0);
            await Case("classic-only",true,false,0);
            await Case("sun-only",false,true,0);
            await Case("one-lamp-only",false,false,1);
            await Case("combined",true,true,1);
            await Case("classic-fallback",true,true,1,true);
            prefix="";
            profile.LightingSpeedMode=false;await Draw("parent-off-warm");var off=await Draw("parent-off");
            profile.LightingSpeedMode=true;await Draw("return-on");profile.LightingSpeedMode=false;
            await Draw("parent-off-return-warm");var returned=await Draw("parent-off-return");
            Check(off.AsSpan().SequenceEqual(returned),"exact parent-off restoration");
            Check(SpeedModeBindings.Current.LightTarget==null&&SpeedModeBindings.Current.Surface==null,"off bindings clear");
            Check(fallbackScreen.AsSpan().SequenceEqual(off),"injected fallback equals actual classic parent-off screen");
            File.WriteAllText(Path.Combine(_out,"data","off-fallback.json"),JsonSerializer.Serialize(new{offDiff=0,fallbackDiff=0,bindingsClear=true}));
            profile.LightingSpeedMode=true;
            // Same actor on four sides of the same source; never move an attended client.
            var directions=new List<System.Numerics.Vector2>();
            foreach(var step in new[]{(X:2,Y:0),(X:0,Y:2),(X:-2,Y:0),(X:0,Y:-2)}) {
                mobile.SetInWorldTile((ushort)(lx+step.X),(ushort)(ly+step.Y),(sbyte)z);
                scene.UpdateDrawPosition=true;game.SetProcess(true);
                for(int i=0;i<12;i++)await Frame();game.SetProcess(false);
                baselineSources=null;baselineSelection=null;noShadowLight=null;
                string position=$"orbit-{step.X}-{step.Y}";prefix=position+"-";
                await Case("no-shadow",false,false,0);
                await Case("one-lamp-only",false,false,1);
                // Preserve each orbit sample and its isolated controls separately.
                var sample=await Sample(position);
                using var trace=JsonDocument.Parse(sample.Trace);
                var offer=trace.RootElement.GetProperty("offers").EnumerateArray().Single(e=>
                    e.GetProperty("X").GetInt32()==lx&&e.GetProperty("Y").GetInt32()==ly);
                var projection=offer.GetProperty("projection");Check(projection.ValueKind==JsonValueKind.Object,"orbit lamp affects actor");
                var direction=new System.Numerics.Vector2(projection.GetProperty("directionX").GetSingle(),projection.GetProperty("directionY").GetSingle());
                var source=offer.GetProperty("shadowPosition");
                float dx=mobile.X-source[0].GetSingle(),dy=mobile.Y-source[1].GetSingle();
                var expected=System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(dx-dy,dx+dy));
                Check(System.Numerics.Vector2.Dot(direction,expected)>.9999f,"direction uses actual offer position");
                directions.Add(direction);
                await Draw(position);
            }
            Check(System.Numerics.Vector2.Dot(directions[0],directions[2])<0
                &&System.Numerics.Vector2.Dot(directions[1],directions[3])<0,"actual point-relative directions reverse around lamp");
            Check(!GUO.Network.NetClient.Socket.IsConnected,"fixture stayed disconnected");
            File.WriteAllText(Path.Combine(_out,"data","acceptance.json"),JsonSerializer.Serialize(new{
                controlsPassed=true,offDiff=0,disconnected=true,visualReviewed=false,
                limitation="Control gates and byte stability only. Native inspection and authorized Gemini review remain required; no visual correction is claimed."}));
            GD.Print("[native-light-attribution] PASS disconnected controls; visual review pending");GetTree().Quit(0);
        }
        catch(Exception error){
            if(!string.IsNullOrWhiteSpace(_out)&&Directory.Exists(Path.Combine(_out,"data")))
                File.WriteAllText(Path.Combine(_out,"data","failure.json"),JsonSerializer.Serialize(new{error=error.ToString(),completed=_rows}));
            GD.PrintErr("[native-light-attribution] FAIL "+error);GetTree().Quit(1);
        }
#endif
    }
}
