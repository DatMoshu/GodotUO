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
        UoPlusOptionsGump panel=null; LiveLightingClockGump live=null;
        try
        {
            var timeline=new LightingClockTimeline();
            Check(timeline.Read(720,true,1,1000)==720,"timeline starts exact seed");
            Check(timeline.Read(720,true,1,61000)==721,"1x one simulated minute per real minute");
            Check(timeline.Read(720,true,60,61000)==721,"speed change no jump");
            Check(timeline.Read(720,true,60,62000)==722,"60x one minute per second");
            Check(timeline.Read(720,false,60,62000)==722,"pause no jump");
            Check(timeline.Read(720,false,60,999000)==722,"paused no drift");
            Check(timeline.Read(720,true,1440,999000)==722,"resume no jump");
            Check(timeline.Read(720,true,1440,1000000)==746,"1440x 24 minutes per second");
            timeline.Seek();Check(timeline.Read(1439,true,60,1000000)==1439,"explicit seek");
            Check(timeline.Read(1439,true,60,1001000)==0,"midnight wrap");
            Check(timeline.Read(1439,true,60,1061000)==60,"multiple day wrap");
            timeline.Seek();Check(timeline.Read(123,false,1,0)==123,"manual minute exact");
            Check(timeline.Read(123,false,1,100000)==123,"manual holds exact");
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
            p=new Profile {LightingSpeedMode=true};
            live=new LiveLightingClockGump(Client.Game.UO.World,p,save:false);
            T Field<T>(string name)=>(T)typeof(LiveLightingClockGump).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(live);
            Field<Checkbox>("_enabled").IsChecked=true;
            Field<HSliderBar>("_minute").Value=19*60+37;
            Check(p.LocalLightingTestTime&&p.LocalLightingTestHour==19&&p.LocalLightingTestMinute==37,"live seek updates without Apply");
            Field<HSliderBar>("_speed").Value=1440;
            Field<Checkbox>("_auto").IsChecked=true;
            double before=LightingTestClock.MinuteAt(p,System.Environment.TickCount64);
            for(int i=0;i<10;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            double after=LightingTestClock.MinuteAt(p,System.Environment.TickCount64);
            Check(after>before&&p.LocalLightingTestAuto&&p.LocalLightingTestSpeed==1440,"live auto clock advances");
            Field<Checkbox>("_auto").IsChecked=false;
            before=LightingTestClock.MinuteAt(p,System.Environment.TickCount64);
            for(int i=0;i<5;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            Check(LightingTestClock.MinuteAt(p,System.Environment.TickCount64)==before,"live pause holds");
            Field<Checkbox>("_enabled").IsChecked=false;
            Check(!LightingTestClock.TryMinute(p,out _),"live off restores shard");
            live.Dispose();
            Check(p.LocalLightingTestHour==19&&p.LocalLightingTestMinute==37,"unsaved close retains live settings");
            p.LocalLightingTestSpeed=9999;p.LocalLightingTestMinute=99;
            copy=JsonSerializer.Deserialize(JsonSerializer.Serialize(p,ProfileJsonContext.DefaultToUse.Profile),ProfileJsonContext.DefaultToUse.Profile);
            Check(copy.LocalLightingTestSpeed==1440&&copy.LocalLightingTestMinute==59,"new bounds/persistence");
            Check(!GUO.Network.NetClient.Socket.IsConnected,"stayed disconnected");
            GD.Print("[Local lighting clock] PASS: 24 hours, parent/default/off parity, server-independent immutable stamp, clamps/persistence, staged Apply/Defaults/Cancel, live seek/auto/speed/pause/off/close, monotonic timeline/wrap; no profile saved or connection");
            GetTree().Quit(0);
        }
        catch(Exception e){GD.PrintErr("[Local lighting clock] FAIL: "+e);GetTree().Quit(1);}
        finally{live?.Dispose();panel?.Dispose();}
    }
    private static void Check(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
}

