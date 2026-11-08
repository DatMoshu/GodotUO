// SPDX-License-Identifier: BSD-2-Clause
// Disconnected fixture, real native Mobile.Draw and supplied client animation art.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class NativeMobileShadowProbe : Node
{
    private string _dir;
    public override void _EnterTree()
    {
        _dir=System.Environment.GetEnvironmentVariable("GUO_SHADOW_PROBE_OUT");
        if(string.IsNullOrEmpty(_dir)) throw new InvalidOperationException("Set GUO_SHADOW_PROBE_OUT to external evidence folder.");
        GUO.Workspace.Workspace.RootOverride=Path.Combine(_dir,"workspace");
        Settings.CustomSettingsFilepath=Path.Combine(_dir,"settings.json");
        File.WriteAllText(Settings.CustomSettingsFilepath,"{}");
    }

    private async Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    private static void Check(bool value,string reason) { if(!value) throw new InvalidOperationException(reason); }

    public override async void _Ready()
    {
        try {
            for(int i=0;Client.Game?.Scene is not LoginScene;i++) { if(i>1200) throw new Exception("login timeout"); await Frame(); }
            var game=Client.Game; var world=game.UO.World;
            var setup=typeof(GUO.Network.NetClient).GetMethod("SetupSocket",BindingFlags.Instance|BindingFlags.NonPublic);
            setup.Invoke(GUO.Network.NetClient.Socket,new[]{Enum.Parse(setup.GetParameters()[0].ParameterType,"TcpSocket")});
            Settings.GlobalSettings.ProfilesPath=Path.GetFullPath(Path.Combine(_dir,"..","data","fixture","profiles"));
            ProfileManager.Load("Offline","shadow-fixture","shadow-fixture");
            var profile=ProfileManager.CurrentProfile;
            profile.GameWindowFullSize=true; profile.TopbarGumpIsDisabled=true;
            profile.UseCircleOfTransparency=false; profile.NoColorObjectsOutOfRange=false;
            profile.LightingSpeedMode=true; profile.SpeedModeSunShadows=true;
            profile.SpeedModeWindowLight=false; profile.SpeedModeLampShadows=6;
            world.CreatePlayer(1); world.MapIndex=0;
            var mobile=world.Player; mobile.Graphic=0x190; mobile.Name="Disconnected shadow fixture";
            mobile.SetInWorldTile(1431,1691,20); mobile.Direction=Direction.South;
            world.ShardClock.Set(12,0,0,Time.Ticks);
            var scene=new GameScene(world); game.SetScene(scene);
            for(int i=0;i<12;i++) await Frame();
            game.SetProcess(false);
            var draw=game.GetType().GetMethod("DrawFrame",BindingFlags.Instance|BindingFlags.NonPublic);
            var rows=new List<object>();
            async Task<byte[]> Draw(string name) {
                draw.Invoke(game,null);
                await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                using var image=GetViewport().GetTexture().GetImage();
                image.SavePng(Path.Combine(_dir,name+".png"));
                return image.GetData();
            }
            async Task<int> Case(string name,bool sitting=false) {
                await Draw(name+"-warm");
                int quads=0,flipped=0,sheared=0;
                var textures=new HashSet<Texture2D>();
                scene.SpeedShadowObserver=(owner,q)=> {
                    if(owner!=mobile) return;
                    quads++;textures.Add(q.Texture);
                    if((q.B-q.A).Cross(q.C-q.A)<0) flipped++;
                    if(Math.Abs(q.A.X-q.C.X)>.01f) sheared++;
                    var anchor=new Vector2(mobile.RealScreenPosition.X+(int)mobile.Offset.X+22,
                        mobile.RealScreenPosition.Y-3+(int)(mobile.Offset.Y-mobile.Offset.Z)+22);
                    var s=q.Project(anchor,new Vector2(2,0),100,.5f,Transform2D.Identity);
                    Check(s.U==q.U && s.V==q.V && s.W==q.W,"exact native UV including flip/section crop");
                    Check(s.A.DistanceTo(anchor+new Vector2(0,-1)*(q.A.X-anchor.X)+new Vector2(2,0)*(anchor.Y-q.A.Y))<.001f,
                         $"native position/contact projection A={q.A} anchor={anchor} result={s.A} expected={anchor+new Vector2(0,-1)*(q.A.X-anchor.X)+new Vector2(2,0)*(anchor.Y-q.A.Y)}");
                };
                try { await Draw(name); } finally { scene.SpeedShadowObserver=null; }
                Check(scene.SpeedMobileQuadCount>0,name+" captured native body-pass quads");
                Check(quads==scene.SpeedMobileQuadCount,"one snapshot per native body-pass quad");
                if(name=="flipped-offset") Check(flipped>0,"native flipped UV path exercised");
                if(sitting) Check(mobile.TryGetSittingInfo(out _) && sheared>0,"native sitting section deformation exercised");
                rows.Add(new{name,quads,textures=textures.Count,flipped,sheared,sitting});
                File.WriteAllText(Path.Combine(_dir,"native-results.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
                return quads;
            }
            int body=await Case("body");
            Item Equip(uint serial,ushort graphic,Layer layer) {
                var item=world.GetOrCreateItem(serial);item.Graphic=graphic;item.Layer=layer;item.Container=mobile.Serial;item.Amount=1;
                mobile.PushToBack(item);return item;
            }
            Equip(0x40000001,0x1F03,Layer.Robe); Equip(0x40000002,0x13B9,Layer.OneHanded);
            int dressed=await Case("dressed"); Check(dressed>body,"equipment contributes native silhouette");
            var mount=Equip(0x40000003,0x3E9F,Layer.Mount);
            await Case("mounted"); Check(mobile.IsMounted,"native mount path exercised");
            world.RemoveItemFromContainer(mount);mount.Destroy();
            var chair=world.GetOrCreateItem(0x40000004);chair.Graphic=0x0A2B;chair.SetInWorldTile(mobile.X,mobile.Y,mobile.Z);
            mobile.Direction=Direction.South;
            await Case("sitting",true);
            chair.RemoveFromTile();chair.Destroy();
            mobile.Direction=Direction.North;mobile.Offset=new Vector3(5.5f,2.25f,1);
            await Case("flipped-offset");
            mobile.Flags|=Flags.Hidden;await Draw("hidden");Check(scene.SpeedMobileQuadCount==0,"hidden caster excluded");
            mobile.Flags&=~Flags.Hidden;mobile.Graphic=0x192;await Draw("dead");Check(scene.SpeedMobileQuadCount==0,"dead caster excluded");
            mobile.Graphic=0x190;
            profile.LightingSpeedMode=false;await Draw("off-warm");var before=await Draw("off-before");
            profile.LightingSpeedMode=true;await Draw("on-again");
            profile.LightingSpeedMode=false;await Draw("off-return-warm");var after=await Draw("off-return");
            Check(before.AsSpan().SequenceEqual(after),"frozen native off return exact pixels");
            GD.Print("[native-mobile-shadow-probe] PASS disconnected=1 body=pass equipment=pass mount=pass sitting=pass hidden=pass dead=pass off-diff=0");
            GetTree().Quit(0);
        }
        catch(Exception error) { GD.PrintErr("[native-mobile-shadow-probe] FAIL "+error);GetTree().Quit(1); }
    }
}
