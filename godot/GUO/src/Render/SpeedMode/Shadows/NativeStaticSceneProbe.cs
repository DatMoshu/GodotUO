// SPDX-License-Identifier: BSD-2-Clause
// Disconnected native draw/contact witness; all state is in an external fixture.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Renderer.DepthWorld;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class NativeStaticSceneProbe : Node
{
    private static void Check(bool value,string why) { if(!value) throw new InvalidOperationException(why); }
    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    public override async void _Ready()
    {
#if !DEBUG
        GD.PrintErr("[native-static-scene] Debug-only disconnected fixture.");GetTree().Quit(1);return;
#else
        try {
            string output=System.Environment.GetEnvironmentVariable("GUO_STATIC_SCENE_OUT");
            Check(!string.IsNullOrWhiteSpace(output)&&Path.IsPathRooted(output),"external GUO_STATIC_SCENE_OUT required");
            output=Path.GetFullPath(output);
            Check(!output.StartsWith(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")),StringComparison.OrdinalIgnoreCase),"external evidence only");
            string home=Path.Combine(output,"data","fixture"),stills=Path.Combine(output,"stills");
            var args=OS.GetCmdlineUserArgs();int cache=Array.IndexOf(args,"--cache-dir");
            Check(cache>=0&&cache+1<args.Length&&Path.GetFullPath(args[cache+1])==Path.Combine(home,"cache"),"explicit isolated cache required");
            Check(!args.Any(a=>a is "--account" or "--password" or "--auto-login" or "--autologin" or "--shard-session"),"no account/session arguments");
            Directory.CreateDirectory(home);Directory.CreateDirectory(stills);
            GUO.Workspace.Workspace.RootOverride=Path.Combine(home,"workspace");
            Settings.CustomSettingsFilepath=Path.Combine(home,"settings.json");File.WriteAllText(Settings.CustomSettingsFilepath,"{}");
            AddChild(new GUO.Host.Main());
            for(int i=0;Client.Game?.Scene is not LoginScene;i++) { Check(i<1200,"login timeout");await Frame(); }
            var game=Client.Game;var world=game.UO.World;
            Check(!GUO.Network.NetClient.Socket.IsConnected,"disconnected socket");
            const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
            var setup=typeof(GUO.Network.NetClient).GetMethod("SetupSocket",hidden);
            setup.Invoke(GUO.Network.NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
            Settings.GlobalSettings.ProfilesPath=Path.Combine(home,"profiles");
            typeof(ProfileManager).GetField("_rootPath",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Settings.GlobalSettings.ProfilesPath);
            ProfileManager.Load("Offline","native-static-1052","native-static-1052");
            Check(Path.GetFullPath(ProfileManager.ProfilePath).StartsWith(home,StringComparison.OrdinalIgnoreCase),"isolated profile");
            var profile=ProfileManager.CurrentProfile;
            profile.DefaultScale=1;profile.WindowBorderless=false;Settings.GlobalSettings.IsWindowMaximized=false;
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            profile.GameWindowFullSize=true;profile.TopbarGumpIsDisabled=true;profile.UseCircleOfTransparency=false;
            profile.NoColorObjectsOutOfRange=false;profile.LightingSpeedMode=true;profile.SpeedModeSunShadows=true;
            profile.SpeedModeWindowLight=false;profile.SpeedModeLampShadows=0;profile.ShadowsEnabled=true;profile.ShadowsStatics=true;
            world.CreatePlayer(1);world.MapIndex=0;world.Player.Graphic=0x190;
            const ushort px=1417,py=1734;
            world.Player.SetInWorldTile(px,py,20);world.ShardClock.Set(12,0,0,Time.Ticks);
            var scene=new GameScene(world);game.SetScene(scene);game.SetWindowSize(1280,720);
            for(int i=0;i<12;i++)await Frame();
            world.Map.GetMapZ(px,py,out sbyte ground,out _);
            world.Player.SetInWorldTile(px,py,(sbyte)(ground+1));
            ushort graphic=0;
            for(ushort id=1;id<0x3fff;id++) {
                var td=game.UO.FileManager.TileData.StaticData[id];
                if(!SunShadowProjection.Eligible(td.Flags,td.Height)||!td.IsLight||td.IsAnimated)continue;
                var art=game.UO.Arts.GetArt(id);
                if(art.Texture!=null) { graphic=id;break; }
            }
            Check(graphic!=0,"native nonanimated eligible lamp witness");
            var item=world.GetOrCreateItem(0x40000001);item.Graphic=graphic;item.Amount=1;
            item.SetInWorldTile(px,py,(sbyte)(ground+1));
            var resident=Static.Create(world,graphic,0,1000000);resident.SetInWorldTile(px+1,py,(sbyte)(ground+1));
            ushort treeGraphic=StaticFilters.TreeTiles.FirstOrDefault(id=>{
                var td=game.UO.FileManager.TileData.StaticData[id];
                return SunShadowProjection.Eligible(td.Flags,td.Height)&&game.UO.Arts.GetArt(id).Texture!=null;
            });
            Check(treeGraphic!=0,"native tree replacement witness");
            var tree=Static.Create(world,treeGraphic,0,1000001);tree.SetInWorldTile(px,py+2,(sbyte)(ground+1));
            scene.UpdateDrawPosition=true;
            // Normal alpha warmup, not direct mutation of native alpha/draw lists.
            for(int i=0;i<32;i++)await Frame();
            game.SetProcess(false);
            game.SetWindowSize(1280,720);await Frame();
            var draw=game.GetType().GetMethod("DrawFrame",hidden);
            var rows=new List<object>();
            var cases=new List<object>();
            bool legacy=System.Environment.GetEnvironmentVariable("GUO_STATIC_EXPECT_LEGACY")=="1";
            async Task<byte[]> Draw(string name) {
                draw.Invoke(game,null);await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var image=GetViewport().GetTexture().GetImage();
                Check(image.GetWidth()==1280&&image.GetHeight()==720,"actual render resolution1280x720");
                Check(image.SavePng(Path.Combine(stills,name+".png"))==Error.Ok,"native PNG");return image.GetData();
            }
            async Task Case(string name,Texture2D expectedTexture=null,int? expectedGap=null,GameObject target=null,int? referenceGraphic=null) {
                target??=item;
                int quads=0,transientQuads=0,residentQuads=0,residentTotal=0;float worst=0;
                scene.SpeedStaticShadowObserver=(owner,q,contact,isResident)=> {
                    if(ReferenceEquals(owner,resident)) { residentTotal++;if(isResident)residentQuads++; }
                    if(!ReferenceEquals(owner,target))return;
                    quads++;if(!isResident)transientQuads++;
                    if(expectedTexture!=null)Check(ReferenceEquals(q.Texture,expectedTexture),name+" exact submitted frame/theme texture");
                    if(expectedGap.HasValue) {
                        float expectedY=target.RealScreenPosition.Y+44-expectedGap.Value;
                        int width=35;
                        float foot=referenceGraphic==-1 ? 17 : FootX((ushort)(referenceGraphic??target.Graphic),out width);
                        float expectedX=target.RealScreenPosition.X+22-(width>>1)+foot;
                        worst=Math.Max(worst,Math.Max(Math.Abs(contact.Y-expectedY),Math.Abs(contact.X-expectedX)));
                        Check(worst<=1,name+" opaque native contact within one native pixel");
                    }
                    rows.Add(new{name,resident=isResident,contact=contact.ToString(),q.A,q.B,q.C,q.U,q.V,q.W,error=worst,
                        cameraZoom=scene.Camera.Zoom,view=scene.Camera.ViewTransform.ToString(),targetGraphic=target.Graphic});
                };
                try { await Draw(name+"-warm");quads=transientQuads=residentQuads=residentTotal=0;await Draw(name); }
                finally { scene.SpeedStaticShadowObserver=null; }
                if(legacy) Check(quads==0&&residentTotal==0&&scene.SpeedSunStampCount>0,name+" baseline reconstructed adapter active");
                else {
                    Check(quads==1&&transientQuads==1,name+" native transient item appears once across both streams");
                    Check(residentQuads==1&&residentTotal==1,name+" native resident static appears once across both streams");
                }
                cases.Add(new{name,legacy,quads,transientQuads,residentQuads,residentTotal,worst,
                    renderWidth=1280,renderHeight=720,cameraZoom=scene.Camera.Zoom,sunStamps=scene.SpeedSunStampCount});
                File.WriteAllText(Path.Combine(output,"data","native-results.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
                File.WriteAllText(Path.Combine(output,"data","cases.json"),JsonSerializer.Serialize(cases,new JsonSerializerOptions{WriteIndented=true}));
            }
            int BottomGap(ushort id) {
                var raw=game.UO.FileManager.Arts.GetArt((uint)(id+ArtLoader.MAX_LAND_DATA_INDEX_COUNT));
                int gap=0;
                for(int y=raw.Height-1;y>=0;y--) {
                    bool occupied=false;
                    for(int x=0;x<raw.Width;x++)if(raw.Pixels[y*raw.Width+x]!=0){occupied=true;break;}
                    if(occupied)break;gap++;
                }
                return gap;
            }
            float FootX(ushort id,out int width) {
                var raw=game.UO.FileManager.Arts.GetArt((uint)(id+ArtLoader.MAX_LAND_DATA_INDEX_COUNT));
                width=raw.Width;
                for(int y=raw.Height-1;y>=0;y--) {
                    int left=-1,right=-1;
                    for(int x=0;x<raw.Width;x++)if(raw.Pixels[y*raw.Width+x]!=0){if(left<0)left=x;right=x;}
                    if(left>=0)return (left+right+1)*.5f;
                }
                throw new InvalidOperationException("reference frame has no opaque contact");
            }
            void Zoom(float zoom) {
                scene.Camera.SetZoomRange(.5f,2.5f,.1f);scene.Camera.Zoom=zoom;
                scene.Camera.Update(true,0,GUO.Input.Mouse.Position);scene.UpdateDrawPosition=true;
            }
            await Case("native",game.UO.Arts.GetArt(graphic).Texture,BottomGap(graphic));
            foreach(float zoom in new[]{.5f,1f,2f}) {
                Zoom(zoom);await Case("zoom-"+zoom.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture),
                    game.UO.Arts.GetArt(graphic).Texture,BottomGap(graphic));
            }
            Zoom(1);
            scene.Camera.PeekingToMouse=true;scene.Camera.Update(true,.1f,new GUO.Compat.Point(950,440));
            await Case("camera-pan",game.UO.Arts.GetArt(graphic).Texture,BottomGap(graphic));
            scene.Camera.PeekingToMouse=false;
            typeof(Camera).GetField("_lerpOffset",hidden).SetValue(scene.Camera,Vector2.Zero);
            Zoom(1);
            sbyte SetFrame(sbyte value) {
                ref var index=ref game.UO.FileManager.Arts.File.GetValidRefEntry(graphic+0x4000);
                sbyte old=index.AnimOffset;index.AnimOffset=value;return old;
            }
            sbyte next=1;
            while(next<120&&game.UO.FileManager.Arts.File.GetValidRefEntry(graphic+next+0x4000).Length<=0)next++;
            Check(next<120,"valid alternate native frame");
            ushort frameGraphic=(ushort)(graphic+next);sbyte previous=SetFrame(next);
            try { await Case("controlled-native-frame-offset",game.UO.Arts.GetArt(frameGraphic).Texture,BottomGap(frameGraphic),referenceGraphic:frameGraphic); }
            finally { SetFrame(previous); }
            profile.TreeToStumps=true;
            await Case("native-tree-replacement",game.UO.Arts.GetArt(Constants.TREE_REPLACE_GRAPHIC).Texture,
                BottomGap(Constants.TREE_REPLACE_GRAPHIC),tree,Constants.TREE_REPLACE_GRAPHIC);
            profile.TreeToStumps=false;
            using var themeImage=Image.CreateEmpty(35,59,false,Image.Format.Rgba8);themeImage.Fill(Colors.Transparent);
            for(int y=0;y<54;y++)for(int x=13;x<21;x++)themeImage.SetPixel(x,y,Colors.White);
            using var theme=ImageTexture.CreateFromImage(themeImage);
            NativeStaticShadow.RegisterTheme(theme,themeImage); // This fixture supplies a CPU-authored theme.
            item.ThemedVariant=new VariantAtlas.VariantImage{Texture=theme,Width=35,Height=59};
            await Case("themed-unequal-frame",theme,5,referenceGraphic:-1);item.ThemedVariant=null;
            profile.LightingSpeedMode=false;await Draw("off-warm");var before=await Draw("off-before");
            profile.LightingSpeedMode=true;await Draw("on-return");profile.LightingSpeedMode=false;
            await Draw("off-return-warm");var after=await Draw("off-return");
            Check(before.AsSpan().SequenceEqual(after),"parent-off exact native return");
            profile.LightingSpeedMode=true;OrderedWorldCapture.InjectSpeedLightFailure=true;
            try {
                await Draw("injected-fallback-warm");var fallback=await Draw("injected-fallback");
                Check(before.AsSpan().SequenceEqual(fallback),"native light transaction failure retains exact classic fallback");
            }
            finally { OrderedWorldCapture.InjectSpeedLightFailure=false;profile.LightingSpeedMode=false; }
            GD.Print($"[native-static-scene] PASS path={(legacy ? "legacy-control" : "native-contact")} themed=1 resident=1 frame-offset=1 tree-replacement=1 zoom=0.5,1,2 pan=1 off-diff=0 fallback-diff=0 resolution=1280x720");
            GetTree().Quit(0);
        }
        catch(Exception error) { GD.PrintErr("[native-static-scene] FAIL "+error);GetTree().Quit(1); }
#endif
    }
}
