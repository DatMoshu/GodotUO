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

public partial class NativeLampSceneProbe : Node
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
        GD.PrintErr("[native-lamp-scene] Unsupported non-Debug probe; native fixture is Debug-only.");
        GetTree().Quit(1);
        return;
#else
        try {
            _out=System.Environment.GetEnvironmentVariable("GUO_LAMP_SCENE_OUT");
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
            ProfileManager.Load("Offline","native-lamp-1041","native-lamp-1041");
            Check(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(_home,StringComparison.OrdinalIgnoreCase),"fixture-only profile path");
            var profile=ProfileManager.CurrentProfile;
            profile.DefaultScale=1;profile.WindowBorderless=false;Settings.GlobalSettings.IsWindowMaximized=false;
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            profile.GameWindowFullSize=true;profile.TopbarGumpIsDisabled=true;
            profile.UseCircleOfTransparency=false;profile.NoColorObjectsOutOfRange=false;profile.DrawRoofs=false;
            profile.LightingSpeedMode=true;profile.SpeedModeWindowLight=false;profile.SpeedModeSunShadows=false;
            profile.SpeedModeLampShadows=0;profile.SpeedModeSoften=0;
            profile.UseCustomLightLevel=true;profile.LightLevel=24;profile.LightLevelType=0;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;world.Player.Name="Disconnected native lamp fixture";


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
            File.WriteAllText(Path.Combine(_out,"data","fixture.json"),JsonSerializer.Serialize(new{lamp,floor,px,py,z,lx,ly,staticCount=index-1000000,profilePath=ProfileManager.ProfilePath},new JsonSerializerOptions{WriteIndented=true}));
            scene.UpdateDrawPosition=true;
            for(int i=0;i<12;i++)await Frame();game.SetProcess(false);
            var draw=game.GetType().GetMethod("DrawFrame",Private);
            var batcher=Field<UltimaBatcher2D>(game,"_uoSpriteBatch");
            OrderedWorldDevice Device()=>Field<OrderedWorldDevice>(batcher.OrderedWorldProbe,"_device");
            SpeedLightFrame Lights()=>Field<SpeedLightFrame>(scene,"_speedLights");
            async Task<byte[]> Draw(string name) {
                draw.Invoke(game,null);await Painted();
                using var image=GetViewport().GetTexture().GetImage();image.SavePng(Path.Combine(_stills,name+".png"));return image.GetData();
            }
            string Offers()=>JsonSerializer.Serialize(Enumerable.Range(0,Lights().Count).Select(i=>{
                var e=Lights().Lights[i];var s=e.Source;return new{s.X,s.Y,s.Z,s.Radius,s.OffsetX,s.OffsetY,s.Energy,curve=s.Curve.ToString(),colour=s.Color.ToString(),e.Flicker,e.PixelCentre,e.PixelRadius};}));
            int FixtureOffer() {
                for(int i=0;i<Lights().Count;i++)if(Lights().Lights[i].Source.X==lx&&Lights().Lights[i].Source.Y==ly)return i;
                return -1;
            }
            async Task<(byte[] Light,byte[] Mask)> Read(int offer=-1) {
                byte[] pixels=null,mask=null;Exception failure=null;
                RenderingServer.CallOnRenderThread(Callable.From(()=>{try{
                    var device=Device();Check(device.SpeedLightReady,"actual core light target ready");pixels=device.ReadSpeedLightRT();
                    if(offer>=0) {
                        var rid=Lights().Lights[offer].MobileShadow;
                        Check(rid.IsValid,"selected fixture native mobile mask valid");
                        var rd=RenderingServer.GetRenderingDevice();mask=rd.TextureGetData(rid,0);
                    }
                }catch(Exception e){failure=e;}}));
                await Painted();if(failure!=null)throw failure;return(pixels,mask);
            }
            int Darkened(byte[] before,byte[] after,byte[] mask=null) {
                Check(before.Length==after.Length,"same actual target layout");int count=0;
                for(int at=0;at<before.Length;at+=8) {
                    if(mask!=null&&mask[at/8]==0)continue;
                    bool lower=false;
                    for(int c=0;c<3;c++) {
                        float a=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(before,at+c*2));
                        float b=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(after,at+c*2));
                        if(float.IsFinite(a)&&float.IsFinite(b)&&a>b+.00005f)lower=true;
                    }
                    if(lower)count++;
                }
                return count;
            }
            var poseQuads=new Dictionary<string,int>();
            async Task<int> Case(string name,bool sitting=false) {
                profile.SpeedModeLampShadows=0;await Draw(name+"-zero-warm");await Draw(name+"-zero");
                Check(!Lights().LampShadowAtlas.IsValid&&Enumerable.Range(0,Lights().Count).All(i=>!Lights().Lights[i].MobileShadow.IsValid),"budget0 no selected masks");
                string actualOffers=Offers();int zeroOffer=FixtureOffer();Check(zeroOffer>=0,"ordinary native fixture lamp offered");
                var before=await Read();await Draw(name+"-zero-repeat");var repeat=await Read();
                Check(before.Light.AsSpan().SequenceEqual(repeat.Light),"actual native lamp control stable");
                profile.SpeedModeLampShadows=6;await Draw(name+"-enabled-warm");
                int quads=0,sheared=0,flipped=0;
                scene.SpeedShadowObserver=(owner,q)=>{if(owner!=mobile)return;quads++;if(Math.Abs(q.A.X-q.C.X)>.01f)sheared++;if((q.B-q.A).Cross(q.C-q.A)<0)flipped++;};
                try{await Draw(name+"-enabled");}finally{scene.SpeedShadowObserver=null;}
                Check(Offers()==actualOffers,"same actual native lamp sources/energy/flicker retained across budgets");
                int offer=FixtureOffer();Check(offer>=0&&Lights().LampShadowAtlas.IsValid&&Lights().Lights[offer].ShadowRow>=0,"fixture offer selected into real lamp atlas");
                Check(quads>0&&quads==scene.SpeedMobileQuadCount,"completed native body-pass geometry witnessed");
                if(sitting)Check(mobile.TryGetSittingInfo(out _)&&sheared>0,"native sitting section deformation witnessed");
                if(name=="flipped-offset")Check(flipped>0,"native flipped geometry witnessed");
                var after=await Read(offer);int maskPixels=after.Mask.Count(v=>v>0);
                Check(after.Mask.Length*8==after.Light.Length&&maskPixels>0,"selected native silhouette mask nonempty/current target dimensions");
                var inputs=Field<SpeedShadowInputs>(scene,"_speedShadows");int row=Lights().Lights[offer].ShadowRow;
                Check(inputs.Mobile[row]?.Count>0,"current native mobile stamps offered to selected lamp row");
                int allDarker=Darkened(before.Light,after.Light),darker=Darkened(before.Light,after.Light,after.Mask);
                _rows.Add(new{name,offers=Lights().Count,fixtureOffer=offer,selectedRow=row,atlas=Lights().LampShadowAtlas.IsValid,maskPixels,stamps=inputs.Mobile[row].Count,quads,sheared,flipped,darkenedPixelsWithinNativeMask=darker,allDarkenedPixels=allDarker,target=Device().Size.ToString(),actualOffers});
                File.WriteAllText(Path.Combine(_out,"data","native-results.json"),JsonSerializer.Serialize(_rows,new JsonSerializerOptions{WriteIndented=true}));
                poseQuads[name]=quads;return darker;
            }
            int body=await Case("body");
            Item Equip(uint serial,ushort graphic,Layer layer) {
                var item=world.GetOrCreateItem(serial);item.Graphic=graphic;item.Layer=layer;item.Container=mobile.Serial;item.Amount=1;mobile.PushToBack(item);return item;
            }
            Equip(0x40000001,0x1F03,Layer.Robe);Equip(0x40000002,0x13B9,Layer.OneHanded);
            int dressed=await Case("dressed");Check(poseQuads["dressed"]>poseQuads["body"],"equipment adds actual native silhouette geometry");
            var mount=Equip(0x40000003,0x3E9F,Layer.Mount);int mounted=await Case("mounted");Check(mobile.IsMounted,"native mount path");
            world.RemoveItemFromContainer(mount);mount.Destroy();
            var chair=world.GetOrCreateItem(0x40000004);chair.Graphic=0x0A2B;chair.SetInWorldTile(mobile.X,mobile.Y,mobile.Z);
            int seated=await Case("sitting",true);chair.RemoveFromTile();chair.Destroy();
            mobile.Direction=Direction.North;mobile.Offset=new Vector3(5.5f,2.25f,1);int flippedOutput=await Case("flipped-offset");
            mobile.Offset=Vector3.Zero;mobile.Direction=Direction.South;
            async Task Excluded(string name) {
                await Draw(name+"-warm");await Draw(name);
                var inputs=Field<SpeedShadowInputs>(scene,"_speedShadows");
                Check(scene.SpeedMobileQuadCount==0&&inputs.Mobile.All(stamps=>stamps==null||stamps.Count==0),name+" no native silhouette stamps");
                Check(Enumerable.Range(0,Lights().Count).All(i=>!Lights().Lights[i].MobileShadow.IsValid),name+" no stale mobile mask");
                Check(FixtureOffer()>=0,"lamp remains offered during exclusion");
            }
            mobile.Flags|=Flags.Hidden;await Excluded("hidden");mobile.Flags&=~Flags.Hidden;
            mobile.Graphic=0x192;await Excluded("dead");mobile.Graphic=0x190;
            game.SetWindowSize(960,640);for(int i=0;i<5;i++)await Frame();int resizedOutput=await Case("resize-960-640");Check(Device().Size==new Vector2I(960,640),"actual native resize");
            profile.LightingSpeedMode=false;await Draw("off-warm");var off=await Draw("off-before");
            profile.LightingSpeedMode=true;await Draw("on-again");profile.LightingSpeedMode=false;await Draw("off-return-warm");var returned=await Draw("off-return");
            Check(off.AsSpan().SequenceEqual(returned),"exact parent-off restoration");
            Check(SpeedModeBindings.Current.LightTarget==null&&SpeedModeBindings.Current.Surface==null,"parent off bindings clear");
            profile.LightingSpeedMode=true;await Draw("unload-active-warm");await Draw("unload-active");
            var retained=Field<SpeedShadowInputs>(scene,"_speedShadows");
            Check(Field<LampShadowFrame>(retained,"_polar")!=null&&Field<ProjectedShadowMaskRd[]>(retained,"_mobile").Any(m=>m!=null),"unload starts with live native lamp resources");
            game.SetScene(new LoginScene(world));await Painted();await Painted();
            Check(Field<LampShadowFrame>(retained,"_polar")==null&&Field<ProjectedShadowMaskRd[]>(retained,"_mobile").All(m=>m==null),"unload releases lamp atlas/mobile masks");
            Check(!GUO.Network.NetClient.Socket.IsConnected,"fixture stayed disconnected");
            bool qualified=body>0&&dressed>0&&mounted>0&&seated>0&&flippedOutput>0&&resizedOutput>0;
            File.WriteAllText(Path.Combine(_out,"data","acceptance.json"),JsonSerializer.Serialize(new{pass=qualified,body,dressed,mounted,seated,flippedOutput,resizedOutput,offDiff=0,bindingsClear=true,unloadReleased=true,hiddenDeadExcluded=true,disconnected=true,limitation=qualified?null:"Native masks exist but at least one pose has no attributable shadow pixels; no guard bypass."},new JsonSerializerOptions{WriteIndented=true}));
            if(!qualified){GD.PrintErr("[native-lamp-scene] BLOCKED conservative missing native receiver-output witness.");GetTree().Quit(2);}
            else{GD.Print("[native-lamp-scene] PASS native offers/selected masks/body/equipment/mount/sitting/shadow-delta/off/resize/unload disconnected=1");GetTree().Quit(0);}
        }
        catch(Exception error){GD.PrintErr("[native-lamp-scene] FAIL "+error);GetTree().Quit(1);}
#endif
    }
}
