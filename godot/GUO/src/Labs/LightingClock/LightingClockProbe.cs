// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using GUO.Configuration;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Renderer.SpeedMode.Shadows;
namespace GUO.Labs.LightingClock;
public partial class LightingClockProbe : Node
{
    public override async void _Ready()
    {
        UoPlusOptionsGump panel=null;
        try
        {
            var p=new Profile(); ShardClockState clock=default;
            clock.Set(15,23,45,1000); var original=clock;
            Check(!LightingTestClock.TryMinute(p,out _),"default off");
            p.LocalLightingTestTime=true;
            Check(!LightingTestClock.TryMinute(p,out _),"parent off");
            p.LightingSpeedMode=true;
            for(int hour=0;hour<24;hour++)
            {
                p.LocalLightingTestHour=hour;
                var result=LightingTestClock.Resolve(p,clock,.4f,2000);
                var expected=SunShadowProjection.FromShardMinute(hour*60);
                Check(result.Direction==expected.Direction&&result.Length==expected.Length&&result.Strength==expected.Strength&&result.Source=="local-test-clock","local projection "+hour);
                Check(LightingTestClock.TryBrightness(p,out float level)&&float.IsFinite(level)&&level>=.1f&&level<=1,"brightness "+hour);
                ShardClockState changed=default;changed.Set(3,0,0,4000);
                Check(result==LightingTestClock.Resolve(p,changed,.9f,9000),"shard-independent "+hour);
            }
            Check(clock.Equals(original),"incoming stamp unchanged");
            p.LocalLightingTestHour=12;LightingTestClock.TryBrightness(p,out float noon);
            p.LocalLightingTestHour=0;LightingTestClock.TryBrightness(p,out float night);
            Check(noon==1&&night==.1f,"noon/night endpoints");
            p.LocalLightingTestTime=false;
            Check(LightingTestClock.Resolve(p,clock,.4f,2000)==SunShadowProjection.Resolve(clock,.4f,2000),"disabled exact valid-shard resolver");
            clock=default;
            Check(LightingTestClock.Resolve(p,clock,.4f,2000)==SunShadowProjection.Resolve(clock,.4f,2000),"disabled exact fallback resolver");
            p.LocalLightingTestHour=-1;Check(p.LocalLightingTestHour==0,"lower clamp");
            p.LocalLightingTestHour=24;Check(p.LocalLightingTestHour==23,"upper clamp");
            p.LocalLightingTestTime=true;
            var copy=JsonSerializer.Deserialize(JsonSerializer.Serialize(p,ProfileJsonContext.DefaultToUse.Profile),ProfileJsonContext.DefaultToUse.Profile);
            Check(copy.LocalLightingTestTime&&copy.LocalLightingTestHour==23,"profile persistence");
            for(int i=0;i<600&&Client.Game?.UO?.Gumps==null;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            for(int i=0;i<90;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            p=new Profile {LightingSpeedMode=true};
            panel=new UoPlusOptionsGump(Client.Game.UO.World,p);
            var toggle=panel.FindControls<ScrollArea>().Single().FindControls<Checkbox>().Single(c=>c.Text=="Local test time (ignore shard clock)");
            var slider=(HSliderBar)typeof(UoPlusOptionsGump).GetField("_localClockHour",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(panel);
            toggle.IsChecked=true;slider.Value=19;
            Check(!p.LocalLightingTestTime&&p.LocalLightingTestHour==12,"UI staging");
            Check(panel.ApplyChanges(save:false)&&p.LocalLightingTestTime&&p.LocalLightingTestHour==19,"UI Apply");
            panel.RestoreDefaults();
            Check(p.LocalLightingTestTime&&p.LocalLightingTestHour==19&&!toggle.IsChecked&&slider.Value==12,"UI Defaults staged");
            Check(panel.ApplyChanges(save:false)&&!p.LocalLightingTestTime&&p.LocalLightingTestHour==12,"UI Defaults Apply");
            toggle.IsChecked=true;slider.Value=7;panel.OnButtonClick(1);
            Check(!p.LocalLightingTestTime&&p.LocalLightingTestHour==12&&panel.IsDisposed,"UI Cancel");
            Check(!GUO.Network.NetClient.Socket.IsConnected,"stayed disconnected");
            GD.Print("[Local lighting clock] PASS: 24 hours, parent/default/off parity, server-independent immutable stamp, clamps/persistence, staged Apply/Defaults/Cancel; no profile saved or connection");
            GetTree().Quit(0);
        }
        catch(Exception e){GD.PrintErr("[Local lighting clock] FAIL: "+e);GetTree().Quit(1);}
        finally{panel?.Dispose();}
    }
    private static void Check(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
}

